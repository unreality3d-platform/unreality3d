using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace U3D
{
    /// <summary>
    /// Controller for Event Gate monetization tool.
    /// Manages timed event access with countdown and payment integration.
    /// </summary>
    public class EventGateController : MonoBehaviour
    {
        [Header("Event Configuration")]
        [SerializeField] private float eventDuration = 3600f; // 1 hour
        [SerializeField] private bool eventActive = true;
        [SerializeField] private bool accessGranted = false;

        [Header("Event Details")]
        [SerializeField] private string eventName = "Special Event";
        [SerializeField] private string eventDescription = "Limited time event access";

        private float timeRemaining;
        private TextMeshProUGUI timerText;
        private Button accessButton;

        private void Start()
        {
            timeRemaining = eventDuration;
            timerText = transform.Find("TimerText")?.GetComponent<TextMeshProUGUI>();
            accessButton = GetComponentInChildren<Button>();

            // Initialize display
            UpdateTimerDisplay();
        }

        private void Update()
        {
            if (eventActive && timeRemaining > 0)
            {
                timeRemaining -= Time.deltaTime;
                UpdateTimerDisplay();

                if (timeRemaining <= 0)
                {
                    EndEvent();
                }
            }
        }

        private void UpdateTimerDisplay()
        {
            if (timerText != null)
            {
                if (timeRemaining > 0)
                {
                    int hours = Mathf.FloorToInt(timeRemaining / 3600);
                    int minutes = Mathf.FloorToInt((timeRemaining % 3600) / 60);
                    int seconds = Mathf.FloorToInt(timeRemaining % 60);

                    if (hours > 0)
                    {
                        timerText.text = $"{hours:00}:{minutes:00}:{seconds:00} remaining";
                    }
                    else
                    {
                        timerText.text = $"{minutes:00}:{seconds:00} remaining";
                    }
                }
                else
                {
                    timerText.text = "Event Ended";
                }
            }
        }

        private void EndEvent()
        {
            eventActive = false;

            if (timerText != null)
            {
                timerText.text = "Event Ended";
            }

            if (accessButton != null)
            {
                accessButton.interactable = false;
                var buttonText = accessButton.GetComponentInChildren<TextMeshProUGUI>();
                if (buttonText != null)
                {
                    buttonText.text = "Event Ended";
                }
            }

            var statusText = transform.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
            if (statusText != null)
            {
                statusText.text = "Event has expired";
            }
        }

        /// <summary>
        /// Grant access after successful payment
        /// </summary>
        public void GrantAccess()
        {
            if (eventActive)
            {
                accessGranted = true;
                Debug.Log("Event access granted!");

                var statusText = transform.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
                if (statusText != null)
                {
                    statusText.text = "Event access granted!";
                }

                if (accessButton != null)
                {
                    accessButton.interactable = false;
                    var buttonText = accessButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (buttonText != null)
                    {
                        buttonText.text = "Access Granted";
                    }
                }
            }
            else
            {
                Debug.LogWarning("Cannot grant access - event is not active");
            }
        }

        /// <summary>
        /// Check if user has access to the event
        /// </summary>
        public bool HasAccess() => accessGranted && eventActive;

        /// <summary>
        /// Set event duration in seconds
        /// </summary>
        public void SetEventDuration(float durationInSeconds)
        {
            eventDuration = durationInSeconds;
            timeRemaining = durationInSeconds;
            UpdateTimerDisplay();
        }

        /// <summary>
        /// Set event details
        /// </summary>
        public void SetEventDetails(string name, string description)
        {
            eventName = name;
            eventDescription = description;

            // Update event info text if available
            var eventInfo = transform.Find("EventInfo")?.GetComponent<TextMeshProUGUI>();
            if (eventInfo != null)
            {
                eventInfo.text = name;
            }
        }

        /// <summary>
        /// Restart the event with new duration
        /// </summary>
        public void RestartEvent(float newDuration = -1)
        {
            if (newDuration > 0)
            {
                eventDuration = newDuration;
            }

            timeRemaining = eventDuration;
            eventActive = true;
            accessGranted = false;

            if (accessButton != null)
            {
                accessButton.interactable = true;
                var buttonText = accessButton.GetComponentInChildren<TextMeshProUGUI>();
                if (buttonText != null)
                {
                    buttonText.text = "Buy Ticket";
                }
            }

            var statusText = transform.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
            if (statusText != null)
            {
                statusText.text = "Ready to accept payments";
            }

            UpdateTimerDisplay();
        }

        /// <summary>
        /// Get remaining time in seconds
        /// </summary>
        public float GetTimeRemaining() => timeRemaining;

        /// <summary>
        /// Check if event is currently active
        /// </summary>
        public bool IsEventActive() => eventActive;

        // Editor validation
        private void OnValidate()
        {
            if (eventDuration < 0)
                eventDuration = 0;
        }
    }
}