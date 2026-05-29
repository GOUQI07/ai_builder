using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AIBuilder
{
    public sealed class AIBuilderCardDrag : MonoBehaviour
    {
        public Action<float> OnDragChanged;
        public Action<int> OnSwipeCompleted;

        [SerializeField] private float followRange = 155f;
        [SerializeField] private float clickDeadZone = 0.08f;
        [SerializeField] private float maxTilt = 8f;
        [SerializeField] private float followSmoothing = 18f;

        private RectTransform rectTransform;
        private Vector2 startPosition;
        private float currentProgress;
        private int lastDirection = 1;
        private bool interactable = true;
        private bool submitted;

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            startPosition = rectTransform.anchoredPosition;
        }

        private void Update()
        {
            if (!interactable || submitted)
            {
                return;
            }

            var targetProgress = GetPointerProgress();
            currentProgress = Mathf.Lerp(currentProgress, targetProgress, 1f - Mathf.Exp(-followSmoothing * Time.deltaTime));
            if (Mathf.Abs(currentProgress) > clickDeadZone)
            {
                lastDirection = currentProgress < 0f ? -1 : 1;
            }

            ApplyProgress(currentProgress);
            OnDragChanged?.Invoke(currentProgress);

            if (WasPrimaryPressedThisFrame())
            {
                ConfirmCurrentSide();
            }
        }

        public void SetInteractable(bool value)
        {
            interactable = value;
            if (!value)
            {
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
            currentProgress = 0f;
            rectTransform.anchoredPosition = startPosition;
            rectTransform.localRotation = Quaternion.identity;
            OnDragChanged?.Invoke(0f);
        }

        private float GetPointerProgress()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null)
            {
                return 0f;
            }

            var screenX = mouse.position.ReadValue().x;
#else
            var screenX = Input.mousePosition.x;
#endif
            if (Screen.width <= 0)
            {
                return 0f;
            }

            return Mathf.Clamp(((screenX / Screen.width) - 0.5f) * 2.15f, -1f, 1f);
        }

        private bool WasPrimaryPressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        private void ApplyProgress(float progress)
        {
            var eased = Mathf.Sign(progress) * Mathf.Pow(Mathf.Abs(progress), 0.82f);
            rectTransform.anchoredPosition = startPosition + new Vector2(eased * followRange, -Mathf.Abs(eased) * 4f);
            rectTransform.localRotation = Quaternion.Euler(0f, 0f, -eased * maxTilt);
        }

        private void ConfirmCurrentSide()
        {
            var direction = Mathf.Abs(currentProgress) <= clickDeadZone ? lastDirection : currentProgress < 0f ? -1 : 1;
            submitted = true;
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
