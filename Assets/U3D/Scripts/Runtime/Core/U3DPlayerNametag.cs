using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using U3D;
using U3D.Net;

namespace U3D.Networking
{
    /// <summary>
    /// Floating name label for another player. Billboards toward the local player's
    /// camera, fades with distance measured from the local player's body rather than the
    /// camera, and hides when line of sight is blocked.
    ///
    /// Lives on a child of the remote avatar prefab, never on its root: this component
    /// writes its own transform, so on the root it would lift and spin the avatar itself.
    ///
    /// It knows nothing about who it represents. The bootstrap hands it the body to hang
    /// over and the name to show when the avatar spawns, and may push a different name in
    /// later through SetDisplayName. The local player carries no nametag — you do not see
    /// your own.
    /// </summary>
    [DefaultExecutionOrder(120)]
    public class U3DPlayerNametag : MonoBehaviour, INetDisplayNameTarget
    {
        [Header("Nametag Configuration")]
        [SerializeField] private float maxDisplayDistance = 30f;
        [SerializeField] private float fadeStartDistance = 20f;
        [SerializeField] private Vector3 worldOffset = new Vector3(0, 2.25f, 0);
        [SerializeField] private bool requireLineOfSight = true;

        [Header("Performance Settings")]
        [SerializeField] private float updateFrequency = 0.1f;
        [SerializeField] private LayerMask lineOfSightLayers = -1;

        [Header("UI References")]
        [SerializeField] private Canvas nametagCanvas;
        [SerializeField] private TextMeshProUGUI playerNameText;
        [SerializeField] private CanvasGroup canvasGroup;

        // Runtime references
        private Transform _subject;
        private U3DPlayerController _localPlayer;
        private Camera _localPlayerCamera;

        // Performance optimization
        private float _lastUpdateTime;
        private float _currentAlpha = 1f;
        private bool _isInitialized = false;
        private bool _hasLineOfSight = true;

        private string _displayName;

        public void Initialize(Transform subject, string displayName)
        {
            _subject = subject;
            _displayName = displayName;

            CreateNametagUI();
            ApplyDisplayName();
            _isInitialized = true;
        }

        /// <summary>
        /// Changes the name shown. Safe to call before or after Initialize, so a real
        /// profile name arriving later replaces the generated one with no other wiring.
        /// </summary>
        public void SetDisplayName(string displayName)
        {
            _displayName = displayName;
            ApplyDisplayName();
        }

        void CreateNametagUI()
        {
            if (nametagCanvas == null)
            {
                var canvasObject = new GameObject("NametagCanvas");
                canvasObject.transform.SetParent(transform);
                canvasObject.transform.localPosition = Vector3.zero;

                nametagCanvas = canvasObject.AddComponent<Canvas>();
                nametagCanvas.renderMode = RenderMode.WorldSpace;
                nametagCanvas.worldCamera = null;

                var canvasRect = nametagCanvas.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(300, 80);
                canvasRect.localScale = new Vector3(0.002f, 0.002f, 0.002f);

                canvasGroup = canvasObject.AddComponent<CanvasGroup>();
            }

            var tmpResources = new TMP_DefaultControls.Resources();

            var uiResources = new DefaultControls.Resources();
            var panelObject = DefaultControls.CreatePanel(uiResources);
            panelObject.name = "NametagPanel";
            panelObject.transform.SetParent(nametagCanvas.transform, false);

            var nametagRect = panelObject.GetComponent<RectTransform>();
            nametagRect.anchorMin = Vector2.zero;
            nametagRect.anchorMax = Vector2.one;
            nametagRect.offsetMin = Vector2.zero;
            nametagRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyPanelStyle(panelObject);

            var panelImage = panelObject.GetComponent<Image>();
            panelImage.raycastTarget = false;

            var nameTextObject = TMP_DefaultControls.CreateText(tmpResources);
            nameTextObject.name = "PlayerName";
            nameTextObject.transform.SetParent(panelObject.transform, false);

            var nameRect = nameTextObject.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = Vector2.one;
            nameRect.offsetMin = new Vector2(10, 10);
            nameRect.offsetMax = new Vector2(-10, -10);

            playerNameText = nameTextObject.GetComponent<TextMeshProUGUI>();
            playerNameText.text = "Player";
            playerNameText.fontSize = 24;
            playerNameText.color = U3DUIStyle.TextColor;
            playerNameText.alignment = TextAlignmentOptions.Center;
            playerNameText.raycastTarget = false;

            playerNameText.enableAutoSizing = true;
            playerNameText.fontSizeMin = 20;
            playerNameText.fontSizeMax = 24;
        }

        /// <summary>
        /// The camera to face is the local player's own and nothing else. A player already
        /// in the room is announced while this machine is still joining, before the local
        /// player exists, so any camera found at that moment is a stand-in: the loading
        /// camera, or a mirror's reflection camera, which is never destroyed and would hold
        /// the label facing the mirror for the rest of the session. Nothing is chosen until
        /// the local player exists, and the choice is checked against that player's camera
        /// every frame.
        /// </summary>
        bool ResolveLocalCamera()
        {
            if (_localPlayer == null)
            {
                _localPlayer = U3DPlayerController.FindLocalPlayer();
                if (_localPlayer == null)
                    return false;
            }

            Transform cameraTransform = _localPlayer.CameraTransform;
            if (cameraTransform == null)
                return false;

            if (_localPlayerCamera == null || _localPlayerCamera.transform != cameraTransform)
            {
                _localPlayerCamera = cameraTransform.GetComponent<Camera>();
                if (_localPlayerCamera == null)
                    return false;

                if (nametagCanvas != null)
                    nametagCanvas.worldCamera = _localPlayerCamera;
            }

            return true;
        }

        // An empty name shows the generic label rather than an empty panel. In ordinary
        // play this does not happen: every remote avatar is given a generated name at
        // spawn, which is what an unnamed player is called.
        void ApplyDisplayName()
        {
            if (playerNameText == null) return;

            playerNameText.text = string.IsNullOrEmpty(_displayName) ? "Player" : _displayName;
        }

        /// <summary>
        /// Runs after the session's render pass at 110 has written the avatar root, so the
        /// root's turn this frame is not carried into the label, and after the controller at
        /// 100 has placed the camera, so the label faces this frame's camera.
        /// </summary>
        void LateUpdate()
        {
            if (!_isInitialized || _subject == null)
                return;

            if (!ResolveLocalCamera())
                return;

            // Position and facing run every frame. The label is a child of a body that
            // turns, so at a tenth of a second the panel would swing with the avatar
            // between updates and snap back. Both are a handful of vector operations; the
            // raycast below is the expensive part and it is what the interval is for.
            UpdatePosition();
            UpdateBillboarding();

            if (Time.time - _lastUpdateTime < updateFrequency)
                return;

            _lastUpdateTime = Time.time;

            UpdateVisibilityAndLineOfSight();
        }

        void UpdatePosition()
        {
            transform.position = _subject.position + worldOffset;
        }

        void UpdateVisibilityAndLineOfSight()
        {
            // Distance fade uses the player body, not camera, so third-person zoom doesn't affect visibility
            float distance = Vector3.Distance(transform.position, _localPlayer.transform.position);

            if (distance > maxDisplayDistance)
            {
                SetAlpha(0f);
                return;
            }

            if (requireLineOfSight)
            {
                _hasLineOfSight = CheckLineOfSight();
                if (!_hasLineOfSight)
                {
                    SetAlpha(0f);
                    return;
                }
            }

            // Behind-camera check still uses camera (this is a view-space check)
            Vector3 directionToNametag = (transform.position - _localPlayerCamera.transform.position).normalized;
            float dotProduct = Vector3.Dot(_localPlayerCamera.transform.forward, directionToNametag);

            if (dotProduct < 0.1f)
            {
                SetAlpha(0f);
                return;
            }

            float alpha = 1f;
            if (distance > fadeStartDistance)
            {
                float fadeRange = maxDisplayDistance - fadeStartDistance;
                float fadeProgress = (distance - fadeStartDistance) / fadeRange;
                alpha = 1f - Mathf.Clamp01(fadeProgress);
            }

            SetAlpha(alpha);
        }

        bool CheckLineOfSight()
        {
            // Line-of-sight raycast still originates from camera (what you can see)
            Vector3 rayOrigin = _localPlayerCamera.transform.position;
            Vector3 rayDirection = (transform.position - rayOrigin).normalized;
            float rayDistance = Vector3.Distance(rayOrigin, transform.position);

            if (Physics.Raycast(rayOrigin, rayDirection, out RaycastHit hit, rayDistance, lineOfSightLayers))
            {
                if (hit.collider.transform == _subject ||
                    hit.collider.transform.IsChildOf(_subject))
                {
                    return true;
                }

                return false;
            }

            return true;
        }

        void UpdateBillboarding()
        {
            if (_localPlayerCamera != null)
            {
                Vector3 directionToCamera = _localPlayerCamera.transform.position - transform.position;
                directionToCamera.y = 0;

                if (directionToCamera != Vector3.zero)
                {
                    transform.rotation = Quaternion.LookRotation(-directionToCamera);
                }
            }
        }

        void SetAlpha(float alpha)
        {
            _currentAlpha = alpha;

            if (canvasGroup != null)
            {
                canvasGroup.alpha = alpha;
            }
        }

        void OnDrawGizmosSelected()
        {
            if (_localPlayerCamera != null && Application.isPlaying)
            {
                Vector3 rayOrigin = _localPlayerCamera.transform.position;
                Vector3 rayEnd = transform.position;

                Gizmos.color = _hasLineOfSight ? Color.green : Color.red;
                Gizmos.DrawLine(rayOrigin, rayEnd);

                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(transform.position, 0.5f);
            }
        }
    }
}