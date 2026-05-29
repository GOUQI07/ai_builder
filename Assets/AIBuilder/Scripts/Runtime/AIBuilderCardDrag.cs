using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AIBuilder
{
    public sealed class AIBuilderCardDrag : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public Action<float> OnDragChanged;
        public Action<int> OnSwipeCompleted;

        [SerializeField] private float followRange = 210f;
        [SerializeField] private float releaseThreshold = 0.58f;
        [SerializeField] private float clickDeadZone = 0.08f;
        [SerializeField] private float sideTapZone = 0.22f;
        [SerializeField] private float tapMaxDistance = 12f;
        [SerializeField] private float maxTilt = 9f;
        [SerializeField] private float settleSmoothing = 18f;

        private RectTransform rectTransform;
        private Vector2 startPosition;
        private Vector2 pointerStartScreen;
        private float currentProgress;
        private int lastDirection = 1;
        private bool interactable = true;
        private bool pointerActive;
        private bool submitted;

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            startPosition = rectTransform.anchoredPosition;
        }

        private void Update()
        {
            if (!interactable || submitted || pointerActive)
            {
                return;
            }

            if (Mathf.Abs(currentProgress) <= 0.001f)
            {
                return;
            }

            currentProgress = Mathf.Lerp(currentProgress, 0f, 1f - Mathf.Exp(-settleSmoothing * Time.deltaTime));
            ApplyProgress(currentProgress);
            OnDragChanged?.Invoke(currentProgress);
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!interactable || submitted)
            {
                return;
            }

            pointerActive = true;
            pointerStartScreen = eventData.position;
            currentProgress = 0f;
            ApplyProgress(currentProgress);
            OnDragChanged?.Invoke(currentProgress);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!interactable || submitted || !pointerActive)
            {
                return;
            }

            var dragWidth = Mathf.Max(80f, Screen.width * 0.28f);
            currentProgress = Mathf.Clamp((eventData.position.x - pointerStartScreen.x) / dragWidth, -1f, 1f);
            if (Mathf.Abs(currentProgress) > clickDeadZone)
            {
                lastDirection = currentProgress < 0f ? -1 : 1;
            }

            ApplyProgress(currentProgress);
            OnDragChanged?.Invoke(currentProgress);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!interactable || submitted || !pointerActive)
            {
                return;
            }

            pointerActive = false;
            var releaseDelta = eventData.position - pointerStartScreen;
            if (Mathf.Abs(currentProgress) >= releaseThreshold)
            {
                ConfirmDirection(currentProgress < 0f ? -1 : 1);
                return;
            }

            if (releaseDelta.magnitude <= tapMaxDistance && TryGetSideTapDirection(eventData.position, out var direction))
            {
                ConfirmDirection(direction);
                return;
            }

            ResetCard();
        }

        public void SetInteractable(bool value)
        {
            interactable = value;
            if (!value)
            {
                pointerActive = false;
                OnDragChanged?.Invoke(0f);
            }
        }

        public void ResetCard()
        {
            if (rectTransform == null)
            {
                rectTransform = GetComponent<RectTransform>();
            }

            submitted = false;
            pointerActive = false;
            currentProgress = 0f;
            rectTransform.anchoredPosition = startPosition;
            rectTransform.localRotation = Quaternion.identity;
            OnDragChanged?.Invoke(0f);
        }

        private bool TryGetSideTapDirection(Vector2 screenPosition, out int direction)
        {
            direction = 0;
            if (Screen.width <= 0)
            {
                return false;
            }

            var normalized = (screenPosition.x / Screen.width) - 0.5f;
            if (Mathf.Abs(normalized) < sideTapZone)
            {
                return false;
            }

            direction = normalized < 0f ? -1 : 1;
            return true;
        }

        private void ApplyProgress(float progress)
        {
            var eased = Mathf.Sign(progress) * Mathf.Pow(Mathf.Abs(progress), 0.82f);
            rectTransform.anchoredPosition = startPosition + new Vector2(eased * followRange, -Mathf.Abs(eased) * 4f);
            rectTransform.localRotation = Quaternion.Euler(0f, 0f, -eased * maxTilt);
        }

        private void ConfirmDirection(int direction)
        {
            direction = direction < 0 ? -1 : 1;
            lastDirection = direction;
            currentProgress = direction;
            submitted = true;
            pointerActive = false;
            interactable = false;
            rectTransform.anchoredPosition = startPosition + new Vector2(direction * (followRange * 0.72f), -10f);
            rectTransform.localRotation = Quaternion.Euler(0f, 0f, -direction * maxTilt);
            OnDragChanged?.Invoke(direction);
            OnSwipeCompleted?.Invoke(direction);
        }

        public int CurrentDirection()
        {
            if (Mathf.Abs(currentProgress) <= clickDeadZone)
            {
                return lastDirection;
            }

            return currentProgress < 0f ? -1 : 1;
        }
    }
}
