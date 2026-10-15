using UnityEngine;

namespace DeformedArmies.AnimationPreview
{
    /// <summary>Right-hand weapon grip and optional left-hand support.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    public sealed class SurvivorWeaponAttachment : MonoBehaviour
    {
        public Animator animator;
        public Transform weapon;
        public Transform rightHand;
        public Transform leftHand;
        public Vector3 rightHandOffset;
        public Quaternion rightHandRotation = Quaternion.identity;
        public Vector3 supportHandPosition;
        public Quaternion supportHandRotation = Quaternion.identity;
        public Transform[] supportFingers = new Transform[0];
        public Quaternion[] supportFingerRotations = new Quaternion[0];
        public string[] freeLeftHandStates = { "reloading", "hit", "dying" };
        public float CurrentSupportWeight { get; private set; }

        private Avatar calibratedAvatar;
        private Quaternion boneToIKGoal = Quaternion.identity;

        private void OnEnable() { calibratedAvatar = null; }
        private void LateUpdate()
        {
            UpdateWeaponPose();
            // Keep the support grip while rifle clips use differing finger poses.
            // Reload and reactions retain their original finger animation.
            int count = Mathf.Min(supportFingers.Length, supportFingerRotations.Length);
            for (int i = 0; i < count; i++)
                if (supportFingers[i] != null)
                    supportFingers[i].localRotation = Quaternion.Slerp(
                        supportFingers[i].localRotation, supportFingerRotations[i], CurrentSupportWeight);
        }

        public void UpdateWeaponPose()
        {
            if (!Ready()) return;
            EvaluateSupportWeight();
            weapon.SetPositionAndRotation(rightHand.TransformPoint(rightHandOffset),
                rightHand.rotation * rightHandRotation);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            if (layerIndex != 0 || !Ready()) return;
            // Humanoid IK goals and imported wrist bones can have different axes.
            if (calibratedAvatar != animator.avatar)
            {
                boneToIKGoal = Quaternion.Inverse(leftHand.rotation) *
                    animator.GetIKRotation(AvatarIKGoal.LeftHand);
                calibratedAvatar = animator.avatar;
            }
            UpdateWeaponPose();
            animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, CurrentSupportWeight);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, CurrentSupportWeight);
            animator.SetIKPosition(AvatarIKGoal.LeftHand, weapon.TransformPoint(supportHandPosition));
            animator.SetIKRotation(AvatarIKGoal.LeftHand,
                weapon.rotation * supportHandRotation * boneToIKGoal);
        }

        private bool Ready()
        {
            if (animator == null) animator = GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman || !animator.isInitialized ||
                animator.runtimeAnimatorController == null || animator.layerCount == 0 || weapon == null)
                return false;
            if (rightHand == null) rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (leftHand == null) leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            return rightHand != null && leftHand != null;
        }

        private void EvaluateSupportWeight()
        {
            float weight = SupportFor(animator.GetCurrentAnimatorStateInfo(0));
            if (animator.IsInTransition(0))
                weight = Mathf.Lerp(weight, SupportFor(animator.GetNextAnimatorStateInfo(0)),
                    Mathf.Clamp01(animator.GetAnimatorTransitionInfo(0).normalizedTime));
            CurrentSupportWeight = weight;
        }

        private float SupportFor(AnimatorStateInfo state)
        {
            if (freeLeftHandStates != null)
                foreach (string name in freeLeftHandStates)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    int hash = Animator.StringToHash(name);
                    if (state.shortNameHash == hash || state.fullPathHash == hash) return 0f;
                }
            return 1f;
        }
    }
}
