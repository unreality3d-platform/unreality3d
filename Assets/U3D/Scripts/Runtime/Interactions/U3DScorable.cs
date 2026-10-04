using UnityEngine;
using UnityEngine.Events;
using TMPro;

namespace U3D
{
    /// <summary>
    /// A score counter with an optional on-screen display. Wire the Add, Subtract, Set and Reset
    /// methods to anything that should change the number — a collectable, a trigger, a button.
    ///
    /// The score is currently counted on each machine separately, so with several people present
    /// each of them sees their own tally rather than a shared one. See the PORT note below.
    /// </summary>
    public class U3DScorable : MonoBehaviour
    {
        [Header("Score Configuration")]
        [Tooltip("Starting score value")]
        [SerializeField] private int startingScore = 0;

        [Tooltip("Amount to add per increment")]
        [SerializeField] private int incrementAmount = 1;

        [Tooltip("Amount to subtract per decrement")]
        [SerializeField] private int decrementAmount = 1;

        [Header("Display")]
        [Tooltip("TextMeshPro component to display the score. Searches this GameObject's hierarchy if not assigned.")]
        [SerializeField] private TextMeshProUGUI scoreText;

        [Tooltip("Format string for score display. Use {0} for the score value.")]
        [SerializeField] private string displayFormat = "{0}";

        [Header("Events")]
        public UnityEvent<int> OnScoreChanged;
        public UnityEvent<int> OnScoreReset;

        // PORT: the score is a room value in spec section 11's sense — the same shape as the
        // collectable's availability, and the legacy RPC-to-authority pattern this replaces was
        // that arbitration spelled differently. Phase 5 routes changes through the reporter per
        // G55, broadcasts the result, and every peer applies it unconditionally per G51. Arrival
        // state must carry the current score or a newcomer's board reads zero.
        // Two questions to settle first, both F-section: whether the score is one number for the
        // room or one per player (today it is one, so a race becomes a group total), and whether a
        // request carries a delta or an absolute value — a delta applied twice double-counts, and
        // F4 makes that format permanent once a build ships. G134, F32, F33
        private int _score;

        private void Start()
        {
            if (scoreText == null)
                scoreText = GetComponentInChildren<TextMeshProUGUI>();

            _score = startingScore;
            UpdateDisplay(_score);
        }

        // ── Public API ----------------------------------------------------------───────

        public void AddScore() => ApplyScore(_score + incrementAmount);

        public void SubtractScore() => ApplyScore(_score - decrementAmount);

        public void AddAmount(int amount) => ApplyScore(_score + amount);

        public void SetScore(int value) => ApplyScore(value);

        /// <summary>
        /// Returns the score to its starting value and fires On Score Reset with that value.
        /// On Score Changed fires as well, since the number moved.
        /// </summary>
        public void ResetScore()
        {
            ApplyScore(startingScore);
            OnScoreReset?.Invoke(_score);
        }

        public int CurrentScore => _score;

        // ── Internal ----------------------------------------------------------─────────

        /// <summary>
        /// The single point where the score changes: stores the value, refreshes the display, then
        /// announces it. The event fires after the value has landed, so a listener reading
        /// CurrentScore gets the new number rather than the previous one.
        /// </summary>
        private void ApplyScore(int value)
        {
            _score = value;
            UpdateDisplay(_score);
            OnScoreChanged?.Invoke(_score);
        }

        private void UpdateDisplay(int value)
        {
            if (scoreText != null)
                scoreText.text = string.Format(displayFormat, value);
        }

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(displayFormat))
            {
                Debug.LogWarning($"{name}: Display Format is empty, so the score will not appear. Use {{0}} where the number should go.", this);
                return;
            }

            // Two checks, because a format can fail two different ways. One throws when the score
            // moves, and is caught here rather than at runtime in a build with no console. The
            // other is valid syntax that simply never shows the number — doubled braces, or no
            // slot at all — which no exception can reveal, so the format is run against two
            // different numbers and the results compared.
            string withZero;
            string withOne;

            try
            {
                withZero = string.Format(displayFormat, 0);
                withOne = string.Format(displayFormat, 1);
            }
            catch (System.FormatException)
            {
                Debug.LogWarning($"{name}: Display Format '{displayFormat}' is not valid. Use {{0}} for the score and nothing else, for example \"Score: {{0}}\".", this);
                return;
            }

            if (withZero == withOne)
            {
                Debug.LogWarning($"{name}: Display Format '{displayFormat}' shows the same text whatever the score is, so the board will never change. Put {{0}} where the number should go, like \"Score: {{0}}\". Doubling the braces prints them as text instead of filling in the number.", this);
            }
        }
    }
}