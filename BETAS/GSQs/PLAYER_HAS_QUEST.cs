﻿using BETAS.Attributes;
using BETAS.Helpers;
using StardewValley;
using StardewValley.Delegates;

namespace BETAS.GSQs;

public static class PlayerHasQuest
{
    // Check whether the player has a quest with the given quest ID.
    [GSQ("PLAYER_HAS_QUEST")]
    public static bool Current(string[] query, GameStateQueryContext context)
    {
        if (!ArgUtilityExtensions.TryGetTokenizable(query, 1, out var playerKey, out var error) ||
            !ArgUtilityExtensions.TryGetTokenizable(query, 2, out var questId, out error))
        {
            return GameStateQuery.Helpers.ErrorResult(query, error);
        }
        
        return GameStateQuery.Helpers.WithPlayer(context.Player, playerKey, target => target.hasQuest(questId));
    }
}