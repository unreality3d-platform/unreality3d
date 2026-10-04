using System.Collections.Generic;

namespace U3D.Editor
{
    /// <summary>
    /// Maps GameObject naming hints to search terms for the Creator Dashboard's
    /// Project Tools search. Returns search terms (durable) rather than specific
    /// tool names (which go stale on rename). The placeholder Inspector uses these
    /// terms to route creators to the live tool catalog.
    /// </summary>
    public static class ComponentSuggestions
    {
        // Maps a substring found in a GameObject name to a search term that will
        // surface relevant tools in Project Tools. Search terms should be short and
        // match across multiple tool titles/descriptions. The Project Tools search
        // does substring matching against both title and description, so generic
        // terms like "grab" or "trigger" cast a wider net than specific tool names.
        private static readonly Dictionary<string, string> nameToSearchTerm = new Dictionary<string, string>
        {
            // Interactions
            {"grabbable", "grab"},
            {"grab", "grab"},
            {"pickup", "grab"},
            {"throw", "throw"},
            {"kick", "kick"},
            {"push", "push"},
            {"climb", "climb"},
            {"swim", "swim"},
            {"interactable", "trigger"},
            {"interact", "trigger"},
            {"trigger", "trigger"},
            {"switch", "trigger"},
            {"door", "trigger"},
            {"button", "trigger"},
            {"click", "trigger"},
            {"spawn", "spawn"},
            {"rideable", "rideable"},
            {"ride", "rideable"},
            {"platform", "rideable"},
            {"vehicle", "steerable"},

            // Navigation
            {"portal", "portal"},
            {"teleport", "portal"},
            {"warp", "portal"},

            // Game systems
            {"quest", "quest"},
            {"mission", "quest"},
            {"objective", "quest"},
            {"inventory", "inventory"},
            {"item", "inventory"},
            {"dialogue", "dialogue"},
            {"conversation", "dialogue"},
            {"npc", "dialogue"},
            {"timer", "timer"},
            {"countdown", "timer"},
            {"achievement", "achievement"},
            {"unlock", "achievement"},
            {"reward", "achievement"},
            {"score", "score"},
            {"checkpoint", "checkpoint"},
            {"respawn", "checkpoint"},
            {"quiz", "quiz"},
            {"question", "quiz"},

            // Media & content
            {"audio", "audio"},
            {"sound", "audio"},
            {"music", "audio"},
            {"video", "video"},
            {"slide", "presentation"},
            {"presentation", "presentation"},
            {"guestbook", "guestbook"},
            {"message", "guestbook"},
            {"sign", "worldspace"},
            {"label", "worldspace"},
            {"url", "url"},
            {"link", "url"},
            {"website", "url"},

            // Monetization
            {"shop", "shop"},
            {"purchase", "purchase"},
            {"buy", "purchase"},
            {"tip", "tip"},
            {"donate", "tip"},
            {"gate", "gate"},
            {"ticket", "event"},
            {"event", "event"},
        };

        /// <summary>
        /// Returns a search term suitable for the Project Tools search field, or
        /// empty string if no keyword in the GameObject's name maps to a known term.
        /// </summary>
        public static string GetSearchTermForGameObject(string gameObjectName)
        {
            if (string.IsNullOrEmpty(gameObjectName)) return "";

            string lowerName = gameObjectName.ToLower();

            foreach (var kvp in nameToSearchTerm)
            {
                if (lowerName.Contains(kvp.Key))
                {
                    return kvp.Value;
                }
            }

            return "";
        }

        [System.Obsolete("Renamed to GetSearchTermForGameObject. This wrapper exists so template projects created before the rename continue to compile.")]
        public static string GetSuggestionForGameObject(string gameObjectName)
        {
            return GetSearchTermForGameObject(gameObjectName);
        }
    }
}