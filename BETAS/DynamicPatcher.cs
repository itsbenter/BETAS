﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BETAS.Helpers;
using BETAS.Models;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Triggers;
using Type = System.Type;

namespace BETAS;

public class DynamicPatcher
{
    public static Harmony DynamicHarmony { get; set; } = null!;

    public static Dictionary<string, KeyValuePair<MethodBase, List<DynamicPatch>>> Prefixes { get; set; } =
        new Dictionary<string, KeyValuePair<MethodBase, List<DynamicPatch>>>();

    public static Dictionary<string, KeyValuePair<MethodBase, List<DynamicPatch>>> Postfixes { get; set; } =
        new Dictionary<string, KeyValuePair<MethodBase, List<DynamicPatch>>>();

    public static bool IsInitialized;

    public static void Initialize(IManifest manifest)
    {
        DynamicHarmony = new Harmony($"{manifest.UniqueID}_DynamicPatcher");
        var AllPatches =
            BETAS.ModHelper.GameContent.Load<List<DynamicPatch>>("Spiderbuttons.BETAS/HarmonyPatches");

        if (AllPatches.Count == 0)
        {
            Log.Trace("No patches found.");
            return;
        }

        foreach (var patch in AllPatches.Where(p =>
                     p.Action is not null || p.Actions is not null || p.ChangeResult is not null))
        {
            var fullName = $"{patch.Target.Type}";
            if (patch.Target.Method is not null) fullName +=
                $".{(patch.Target.IsGetter ? "get_" : patch.Target.IsSetter ? "set_" : "")}{patch.Target.Method}, {patch.Target.Assembly}";
            else fullName += $"..ctor, {patch.Target.Assembly}";
            
            var patchType = patch.PatchType.Equals("Prefix", StringComparison.OrdinalIgnoreCase) ? Prefixes : Postfixes;
            if (patchType.ContainsKey(fullName))
            {
                patchType[fullName].Value.Add(patch);
                continue;
            }

            var patchMethod = GetMethodFromString(patch.Target, out string? error);
            if (patchMethod is null)
            {
                Log.Error($"failed to get method from Target in dynamic patch '{patch.Id}': {error}");
                continue;
            }

            patchType.TryAdd(fullName,
                new KeyValuePair<MethodBase, List<DynamicPatch>>(patchMethod, new List<DynamicPatch> { patch }));
        }

        var dynFactory = AccessTools.Method(typeof(DynamicPatcher), nameof(DynamicFactory));
        try
        {
            foreach (var prefix in Prefixes)
            {
                DynamicHarmony.Patch(original: prefix.Value.Key, prefix: new HarmonyMethod(dynFactory));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to dynamically patch prefixes: {ex}");
        }

        try
        {
            foreach (var postfix in Postfixes)
            {
                DynamicHarmony.Patch(original: postfix.Value.Key, postfix: new HarmonyMethod(dynFactory));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to dynamically patch postfixes: {ex}");
        }

        IsInitialized = true;
    }

    public static void Reset(IManifest manifest)
    {
        IsInitialized = false;
        Prefixes.Clear();
        Postfixes.Clear();
        DynamicHarmony.UnpatchAll(DynamicHarmony.Id);

        Log.Trace("DynamicPatcher has been reset.");

        Initialize(manifest);
    }

    public static DynamicMethod? DynamicFactory(MethodBase? method)
    {
        var methodFullName = $"{method?.DeclaringType}.{method?.Name}, {method?.DeclaringType?.Assembly.GetName().Name}";
        if (!Prefixes.TryGetValue(methodFullName, out var patches))
        {
            if (!Postfixes.TryGetValue(methodFullName, out patches))
            {
                Log.Warn($"No dynamic patches found for method '{methodFullName}'");
                return null;
            }
        }

        if (!TryGetMethodInfo(method, out var returnType, out var declaringType, out var parameterList,
                out var parseMethod))
        {
            Log.Error($"Failed to get method info for dynamic patch '{patches.Value.First().Id}'");
            return null;
        }

        var paramTypes = parameterList.Select(p => p.ParameterType).ToArray();
        if (returnType != typeof(void)) paramTypes = new[] { returnType.MakeByRefType() }.Concat(paramTypes).ToArray();

        DynamicMethod dynamicMethod =
            new DynamicMethod($"SPU_{method?.Name}_{patches.Value.First().PatchType}", typeof(void), paramTypes,
                declaringType);

        if (returnType != typeof(void)) dynamicMethod.DefineParameter(1, ParameterAttributes.None, "__result");
        foreach (var param in parameterList)
        {
            dynamicMethod.DefineParameter(param.Position + (returnType != typeof(void) ? 2 : 1), param.Attributes,
                param.ToString().Split(" ")[1]);
        }

        ILGenerator il = dynamicMethod.GetILGenerator();

        foreach (var patchInfo in patches.Value)
        {
            Log.Trace($"Adding {patchInfo.PatchType} dynamic patch '{patchInfo.Id}' to method '{method?.Name}'");
            var skipBecauseConditionsLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldstr, patchInfo.Condition ?? "true");
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(GameStateQuery), nameof(GameStateQuery.CheckConditions),
                new Type[]
                {
                    typeof(string), typeof(GameLocation), typeof(Farmer), typeof(Item), typeof(Item), typeof(Random),
                    typeof(HashSet<string>)
                }));
            il.Emit(OpCodes.Brfalse, skipBecauseConditionsLabel);

            if (patchInfo.ChangeResult is not null && returnType != typeof(void) &&
                patchInfo.PatchType.Equals("Postfix", StringComparison.OrdinalIgnoreCase))
            {
                OpCode[] ops = returnType.Name.ToUpper() switch
                {
                    "SBYTE" => [OpCodes.Ldind_I1, OpCodes.Stind_I1],
                    "BYTE" => [OpCodes.Ldind_I1, OpCodes.Stind_I1],
                    "BOOLEAN" => [OpCodes.Ldind_I1, OpCodes.Stind_I1],
                    "INT16" => [OpCodes.Ldind_I2, OpCodes.Stind_I2],
                    "UINT16" => [OpCodes.Ldind_I2, OpCodes.Stind_I2],
                    "INT32" => [OpCodes.Ldind_I4, OpCodes.Stind_I4],
                    "UINT32" => [OpCodes.Ldind_I4, OpCodes.Stind_I4],
                    "INT64" => [OpCodes.Ldind_I8, OpCodes.Stind_I8],
                    "UINT64" => [OpCodes.Ldind_I8, OpCodes.Stind_I8],
                    "SINGLE" => [OpCodes.Ldind_R4, OpCodes.Stind_R4],
                    "DOUBLE" => [OpCodes.Ldind_R8, OpCodes.Stind_R8],
                    _ => [OpCodes.Ldind_Ref, OpCodes.Stind_Ref]
                };

                il.Emit(OpCodes.Ldarg_0);
                if (!patchInfo.ChangeResult.Operation.Equals("ASSIGN", StringComparison.OrdinalIgnoreCase))
                {
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(ops[0]);
                    il.Emit(OpCodes.Ldstr, patchInfo.ChangeResult.Value);

                    if (returnType != typeof(string) && parseMethod is not null)
                    {
                        il.Emit(OpCodes.Call, parseMethod);
                        il.Emit(patchInfo.ChangeResult.Operation.ToUpper() switch
                        {
                            "ADD" => OpCodes.Add,
                            "SUBTRACT" => OpCodes.Sub,
                            "MULTIPLY" => OpCodes.Mul,
                            "DIVIDE" => OpCodes.Div,
                            _ => OpCodes.Add
                        });
                    }
                    else
                    {
                        il.Emit(OpCodes.Call, AccessTools.Method(typeof(string), nameof(string.Concat),
                            new Type[] { typeof(string), typeof(string) }));
                    }
                }
                else
                {
                    il.Emit(OpCodes.Ldstr, patchInfo.ChangeResult.Value);
                    if (returnType != typeof(string) && parseMethod is not null) il.Emit(OpCodes.Call, parseMethod);
                }

                il.Emit(ops[1]);
            }

            if (patchInfo.Action is not null)
            {
                il.Emit(OpCodes.Ldstr, patchInfo.Id);
                il.Emit(OpCodes.Ldstr, patchInfo.Action);
                il.Emit(OpCodes.Call,
                    AccessTools.Method(typeof(DynamicPatcher), nameof(TryRunSimplePatchAction)));
            }
            
            if (patchInfo.Actions is not null)
            {
                foreach (var action in patchInfo.Actions)
                {
                    il.Emit(OpCodes.Ldstr, patchInfo.Id);
                    il.Emit(OpCodes.Ldstr, action);
                    il.Emit(OpCodes.Call,
                        AccessTools.Method(typeof(DynamicPatcher), nameof(TryRunSimplePatchAction)));
                }
            }
            
            il.Emit(OpCodes.Nop);
            il.MarkLabel(skipBecauseConditionsLabel);
        }

        il.Emit(OpCodes.Ret);

        return dynamicMethod;
    }

    public static void TryRunSimplePatchAction(string patchID, string action)
    {
        if (TriggerActionManager.TryRunAction(action, out var error, out _)) return;
        Log.Warn($"Failed to run action '{action}' for dynamic patch '{patchID}': {error}");
    }

    public static bool TryGetMethodInfo(MethodBase? method, [NotNullWhen(true)] out Type? returnType, [NotNullWhen(true)] out Type? declaringType,
        out ParameterInfo[] parameterList, out MethodInfo? parseMethod)
    {
        if (method is null)
        {
            returnType = typeof(void);
            declaringType = null;
            parameterList = [];
            parseMethod = null;
            return false;
        }

        returnType = method is MethodInfo info ? info.ReturnType : typeof(void);
        declaringType = method.DeclaringType!;
        parameterList = method.GetParameters();
        parseMethod = returnType != typeof(void)
            ? returnType.GetMethod("Parse", new Type[] { typeof(string) })
            : null;

        return true;
    }

    public static Type GetTypeFromString(string typeName)
    {
        return typeName.ToLower() switch
        {
            "int" => typeof(int),
            "float" => typeof(float),
            "double" => typeof(double),
            "string" => typeof(string),
            "bool" => typeof(bool),
            "byte" => typeof(byte),
            "sbyte" => typeof(sbyte),
            "short" => typeof(short),
            "ushort" => typeof(ushort),
            "uint" => typeof(uint),
            "long" => typeof(long),
            "ulong" => typeof(ulong),
            "char" => typeof(char),
            _ => AccessTools.TypeByName(typeName)
        };
    }

    public static MethodBase? GetMethodFromString(TargetMethod target, out string? error)
    {
        if (string.IsNullOrWhiteSpace(target.Type))
        {
            error = "the type name can't be empty";
            return null;
        }
        
        var paramTypes = target.Parameters.Select(GetTypeFromString).ToArray();
        
        if (string.IsNullOrWhiteSpace(target.Method))
        {
            var type = AccessTools.TypeByName(target.Type);
            if (type is null)
            {
                error = $"could not find type '{target.Type}' in assembly '{target.Assembly}'";
                return null;
            }
            
            var ctor = AccessTools.Constructor(type, paramTypes);
            if (ctor is not null)
            {
                error = null;
                return ctor;
            }
            
            error = $"could not find constructor for type '{target.Type}' with the specified parameters ({string.Join(", ", target.Parameters)}) in assembly '{target.Assembly}'";
        }

        var methodName = $"{target.Type}:{target.Method}";

        var result = target switch
        {
            { IsGetter: true } => AccessTools.PropertyGetter(methodName),
            { IsSetter: true } => AccessTools.PropertySetter(methodName),
            _ => AccessTools.Method(methodName, paramTypes.Length == 0 ? null : paramTypes)
        };

        if (result is not null)
        {
            error = null;
            return result;
        }

        error = paramTypes.Length switch
        {
            0 =>
                $"could not find method '{target.Method}' with no parameters on type '{target.Type}' in assembly '{target.Assembly}'",
            _ =>
                $"could not find method '{target.Method}' with the specified parameters ({string.Join(", ", target.Parameters)}) on type '{target.Type}' in assembly '{target.Assembly}'"
        };
        return null;
    }
}