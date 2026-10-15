using UnityEngine;

namespace DeformedArmies.AnimationPreview
{
    /// <summary>Mouse-operated animation preview, independent of player and network input.</summary>
    [DisallowMultipleComponent]
    public sealed class SurvivorAnimationPreview : MonoBehaviour
    {
        public Animator animator;
        public string[] stateNames = new string[0];
        public string[] labels = new string[0];
        [Min(0f)] public float fadeDuration = 0.15f;
        public bool showControls = true;
        public int currentIndex;
        public bool autoCycle;
        [Min(0.25f)] public float holdSeconds = 4f;

        private float elapsed;
        private float observedStateLength;
        private Vector2 scrollPosition;
        private GUIStyle titleStyle;
        private GUIStyle wrappedLabelStyle;
        private GUIStyle buttonStyle;

        private int StateCount => stateNames == null ? 0 : stateNames.Length;
        private bool CanPlay => animator != null && animator.isActiveAndEnabled &&
            animator.runtimeAnimatorController != null && animator.layerCount > 0;

        private void Awake()
        {
            if (animator == null)
                animator = GetComponentInChildren<Animator>(true);
        }

        private void Start()
        {
            if (StateCount > 0)
                Play(Mathf.Clamp(currentIndex, 0, StateCount - 1));
        }

        public void Play(int index)
        {
            if (!CanPlay || index < 0 || index >= StateCount || string.IsNullOrEmpty(stateNames[index]))
                return;

            currentIndex = index;
            elapsed = 0f;
            observedStateLength = 0f;
            animator.CrossFadeInFixedTime(stateNames[index], Mathf.Max(0f, fadeDuration), 0, 0f);
        }

        public void Replay()
        {
            Play(currentIndex);
        }

        private void Update()
        {
            Step(Time.deltaTime);
        }

        /// <summary>Advances optional cycling; disabled or unavailable Animators are left untouched.</summary>
        public void Step(float deltaTime)
        {
            if (!autoCycle)
            {
                elapsed = 0f;
                return;
            }

            if (!CanPlay || StateCount == 0 || deltaTime <= 0f ||
                float.IsNaN(deltaTime) || float.IsInfinity(deltaTime))
                return;

            elapsed += deltaTime;
            // During a cross-fade, the current state can still be the previous animation.
            if (!animator.IsInTransition(0))
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                if (currentIndex >= 0 && currentIndex < StateCount && state.IsName(stateNames[currentIndex]) &&
                    !float.IsNaN(state.length) && !float.IsInfinity(state.length))
                    observedStateLength = Mathf.Max(observedStateLength, state.length);
            }

            float duration = Mathf.Max(Mathf.Max(0.25f, holdSeconds), observedStateLength + 0.25f);
            if (!animator.IsInTransition(0) && elapsed >= duration)
                Play((Mathf.Clamp(currentIndex, 0, StateCount - 1) + 1) % StateCount);
        }

        private string LabelFor(int index)
        {
            if (index < 0 || index >= StateCount)
                return "未选择 / No animation";
            if (labels != null && index < labels.Length && !string.IsNullOrEmpty(labels[index]))
                return labels[index];
            return stateNames[index];
        }

        private void InitializeStyles()
        {
            if (titleStyle != null)
                return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold, wordWrap = true };
            wrappedLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 13, wordWrap = true, alignment = TextAnchor.MiddleLeft };
            buttonStyle.padding = new RectOffset(9, 6, 7, 7);
        }

        private void OnGUI()
        {
            if (!showControls)
                return;

            InitializeStyles();
            float width = Mathf.Min(260f, Mathf.Max(140f, Screen.width - 24f));
            float height = Mathf.Max(80f, Screen.height - 24f);
            GUILayout.BeginArea(new Rect(12f, 12f, width, height), GUI.skin.box);
            GUILayout.Label("动作检查 / Animation Preview", titleStyle);
            GUILayout.Label("当前 / Current: " + LabelFor(currentIndex), wrappedLabelStyle);

            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && CanPlay && StateCount > 0;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("上一项 / Prev"))
                Play((Mathf.Clamp(currentIndex, 0, StateCount - 1) - 1 + StateCount) % StateCount);
            if (GUILayout.Button("下一项 / Next"))
                Play((Mathf.Clamp(currentIndex, 0, StateCount - 1) + 1) % StateCount);
            GUILayout.EndHorizontal();
            if (GUILayout.Button("重播当前动作 / Replay"))
                Replay();
            autoCycle = GUILayout.Toggle(autoCycle, "自动轮播 / Auto cycle");
            GUI.enabled = previousEnabled;

            if (!CanPlay)
                GUILayout.Label("请启用角色 Animator，并指定动画控制器。", wrappedLabelStyle);
            else if (StateCount == 0)
                GUILayout.Label("尚未配置动画列表。", wrappedLabelStyle);

            GUILayout.Space(5f);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, true);
            GUI.enabled = previousEnabled && CanPlay;
            for (int i = 0; i < StateCount; i++)
            {
                string prefix = i == currentIndex ? "▶ " : "";
                if (GUILayout.Button(prefix + (i + 1) + ". " + LabelFor(i), buttonStyle, GUILayout.MinHeight(36f)))
                    Play(i);
            }
            GUI.enabled = previousEnabled;
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
