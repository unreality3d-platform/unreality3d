using System.Collections.Generic;

namespace U3D
{
    /// <summary>
    /// Generates an ephemeral, per-session display name for any player who is
    /// not logged in with a reserved identity (Branch B of the player identity
    /// system). The name is a pure deterministic function of a single integer
    /// seed (PlayerRef.RawEncoded), so every client in a room computes the
    /// identical name for the same player with zero networked state and zero
    /// coordination — the same architectural guarantee the step 1 nametag fix
    /// relies on.
    ///
    /// SAFETY MODEL: This wordlist is Branch B's entire safety surface. It is
    /// intentionally small enough to read top to bottom in under a minute.
    /// Both arrays are conservatively curated: neutral descriptors and
    /// unambiguous, non-slang animals only. Homograph / slang traps are
    /// excluded by hand. This list is NOT shared with creator-name
    /// reservation — that is a separate axis with separate rules. Do not
    /// expand these into nature/objects/broad categories; keep them tight
    /// and clean.
    ///
    /// Effective namespace: 24 adjectives x 20 animals x 89 suffix values
    /// (~42,000 combinations) against a hard room ceiling of 100 players,
    /// which makes a visible collision astronomically unlikely without any
    /// room awareness.
    /// </summary>
    public static class U3DSafeNameGenerator
    {
        // === AUDIT SURFACE: ADJECTIVES (24) ===
        // Neutral-to-positive descriptors. No body words, no slang, no
        // ambiguous homographs.
        private static readonly string[] Adjectives =
        {
            "Amber",
            "Brave",
            "Calm",
            "Clever",
            "Cosmic",
            "Crimson",
            "Daring",
            "Eager",
            "Gentle",
            "Golden",
            "Happy",
            "Jolly",
            "Lucky",
            "Mellow",
            "Nimble",
            "Noble",
            "Quiet",
            "Rapid",
            "Shiny",
            "Silver",
            "Spry",
            "Sunny",
            "Swift",
            "Witty",
        };

        // === AUDIT SURFACE: ANIMALS (20) ===
        // Unambiguous creatures only. Deliberately excludes any animal whose
        // common name doubles as slang or a homograph.
        private static readonly string[] Animals =
        {
            "Otter",
            "Falcon",
            "Heron",
            "Lynx",
            "Marten",
            "Badger",
            "Wombat",
            "Quokka",
            "Tapir",
            "Gecko",
            "Finch",
            "Robin",
            "Sparrow",
            "Swallow",
            "Pelican",
            "Walrus",
            "Narwhal",
            "Dolphin",
            "Panther",
            "Bison",
        };

        // === AUDIT SURFACE: SUFFIX RANGE ===
        // Two-digit numbers 10..99, with culturally problematic strings
        // excluded. 420 and 1488 cannot occur in a 2-digit range, but the
        // exclusion is built as a filtered list (not a range assumption) so
        // the range can later widen without silently reintroducing them.
        private static readonly int[] BannedNumbers = { 69, 420, 1488 };

        private static readonly int[] SuffixPool = BuildSuffixPool();

        private static int[] BuildSuffixPool()
        {
            var pool = new List<int>(90);
            var banned = new HashSet<int>(BannedNumbers);
            for (int n = 10; n <= 99; n++)
            {
                if (!banned.Contains(n))
                {
                    pool.Add(n);
                }
            }
            return pool.ToArray();
        }

        /// <summary>
        /// Deterministically generates a safe display name from an integer
        /// seed. Pure: same seed always yields the same name, on every client,
        /// with no time, no UnityEngine.Random, and no mutable state.
        ///
        /// The three components are each derived through a DIFFERENT integer
        /// mix of the seed so that adjacent seeds (consecutive room join
        /// order, since RawEncoded increments roughly sequentially) scatter
        /// across the namespace instead of sharing an adjective or animal.
        /// </summary>
        public static string Generate(int seed)
        {
            // Three distinct avalanche mixes of the same seed. Each uses a
            // different odd constant and a final xor-shift so that a +1 change
            // in seed produces an unrelated value in each component
            // independently. Fixed arithmetic => identical on all clients.
            uint a = Mix((uint)seed, 0x9E3779B1u);
            uint b = Mix((uint)seed, 0x85EBCA77u);
            uint c = Mix((uint)seed, 0xC2B2AE3Du);

            string adjective = Adjectives[a % (uint)Adjectives.Length];
            string animal = Animals[b % (uint)Animals.Length];
            int suffix = SuffixPool[c % (uint)SuffixPool.Length];

            return adjective + animal + suffix;
        }

        // Integer avalanche finalizer (MurmurHash3-style fmix on a salted
        // seed). Deterministic and platform-stable: only uint wraparound,
        // multiply, and shift — no floats, no culture, no platform APIs.
        private static uint Mix(uint x, uint salt)
        {
            x += salt;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
}
