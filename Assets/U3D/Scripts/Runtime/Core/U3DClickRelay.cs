using UnityEngine;
using UnityEngine.EventSystems;

namespace U3D.UI
{
    /// <summary>
    /// Forwards a pointer click to code. Handles click only, unlike EventTrigger, which also takes
    /// drag and scroll events from whatever it sits on.
    /// </summary>
    public class U3DClickRelay : MonoBehaviour, IPointerClickHandler
    {
        public event System.Action Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            Clicked?.Invoke();
        }
    }
}