// SimpleVRIK.cs
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lightweight VR full-body IK for a humanoid rig: drives the avatar from an HMD target and
/// two hand targets, with no external asset dependency.
///
/// The serialized layout of <see cref="references"/> and <see cref="solver"/> mirrors the field
/// names this project's scene was already wired with, so swapping the script reference keeps
/// every bone and target assignment.
///
/// Solve order (LateUpdate): restore bind pose -> body heading -> pelvis -> spine -> head
/// -> arms (analytic two-bone IK) -> legs (optional procedural stepping).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(500)]
public class SimpleVRIK : MonoBehaviour
{
    /// <summary>How a target transform's rotation maps onto the bone it drives.</summary>
    public enum RotationMode
    {
        // The target's world rotation is used as the bone rotation (VRIK semantics: the target
        // is a dummy anchor already oriented to match the bone).
        UseTargetRotation = 0,
        // The bind-pose relationship between the bone and the body heading is preserved, and the
        // target's rotation replaces the body heading. Calibration-free, works with raw anchors.
        RelativeToBody = 1,
    }

    public enum LegMode
    {
        // Legs simply hang off the pelvis. Cheapest, feet are not grounded.
        FollowPelvis = 0,
        // Feet are planted on the ground and take procedural steps when the body moves away.
        PlantFeet = 1,
    }

    // ---------------- Serialized data ----------------

    [Serializable]
    public class References
    {
        public Transform root;
        public Transform pelvis;
        public Transform spine;
        public Transform chest;
        public Transform neck;
        public Transform head;
        public Transform leftShoulder;
        public Transform leftUpperArm;
        public Transform leftForearm;
        public Transform leftHand;
        public Transform rightShoulder;
        public Transform rightUpperArm;
        public Transform rightForearm;
        public Transform rightHand;
        public Transform leftThigh;
        public Transform leftCalf;
        public Transform leftFoot;
        public Transform leftToes;
        public Transform rightThigh;
        public Transform rightCalf;
        public Transform rightFoot;
        public Transform rightToes;

        /// <summary>Bones the solver cannot run without.</summary>
        public bool IsValid(out string missing)
        {
            if (pelvis == null) { missing = "pelvis"; return false; }
            if (head == null) { missing = "head"; return false; }
            if (leftUpperArm == null) { missing = "leftUpperArm"; return false; }
            if (leftForearm == null) { missing = "leftForearm"; return false; }
            if (leftHand == null) { missing = "leftHand"; return false; }
            if (rightUpperArm == null) { missing = "rightUpperArm"; return false; }
            if (rightForearm == null) { missing = "rightForearm"; return false; }
            if (rightHand == null) { missing = "rightHand"; return false; }
            missing = null;
            return true;
        }

        public void AutoDetect(Animator animator)
        {
            if (animator == null || !animator.isHuman) return;
            root = animator.transform;
            pelvis = animator.GetBoneTransform(HumanBodyBones.Hips);
            spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            chest = animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (chest == null) chest = animator.GetBoneTransform(HumanBodyBones.Chest);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            leftShoulder = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
            leftUpperArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            leftForearm = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            rightShoulder = animator.GetBoneTransform(HumanBodyBones.RightShoulder);
            rightUpperArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            rightForearm = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            leftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            leftCalf = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            leftToes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
            rightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            rightCalf = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
            rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            rightToes = animator.GetBoneTransform(HumanBodyBones.RightToes);
        }
    }

    [Serializable]
    public class Spine
    {
        [Tooltip("HMD transform (e.g. CenterEyeAnchor)")]
        public Transform headTarget;
        [Range(0f, 1f), Tooltip("How exactly the head bone is pulled onto the target position")]
        public float positionWeight = 1f;
        [Range(0f, 1f), Tooltip("How exactly the head bone takes the target rotation")]
        public float rotationWeight = 1f;
        public RotationMode headRotationMode = RotationMode.UseTargetRotation;
        [Tooltip("Extra euler offset applied to the head when headRotationMode is UseTargetRotation")]
        public Vector3 headRotationOffset = Vector3.zero;

        [Space(4)]
        [Tooltip("Optional explicit pelvis target (hip tracker). Leave empty to derive it from the head.")]
        public Transform pelvisTarget;
        [Range(0f, 1f)] public float pelvisPositionWeight = 0f;
        [Range(0f, 1f)] public float pelvisRotationWeight = 0f;

        [Space(4)]
        [Range(0f, 1f), Tooltip("How quickly the body heading eases toward the head heading")]
        public float bodyRotStiffness = 0.1f;
        [Range(0f, 180f), Tooltip("The body is dragged along once the head yaws further than this")]
        public float maxRootAngle = 25f;

        [Space(4)]
        [Range(0f, 1f), Tooltip("Share of the head rotation delta taken by the spine bone")]
        public float spineBend = 0.3f;
        [Range(0f, 1f), Tooltip("Share taken by the chest bone")]
        public float chestBend = 0.3f;
        [Range(0f, 1f), Tooltip("Share taken by the neck bone")]
        public float neckBend = 0.5f;
    }

    [Serializable]
    public class Arm
    {
        [Tooltip("Hand target (e.g. a dummy anchor under the controller anchor)")]
        public Transform target;
        [Range(0f, 1f)] public float positionWeight = 1f;
        [Range(0f, 1f)] public float rotationWeight = 1f;
        public RotationMode handRotationMode = RotationMode.UseTargetRotation;
        [Tooltip("Extra euler offset applied to the hand when handRotationMode is UseTargetRotation")]
        public Vector3 rotationOffset = Vector3.zero;

        [Range(0f, 1f), Tooltip("How much the shoulder turns toward the target before the arm solves")]
        public float shoulderRotationWeight = 0f;

        [Tooltip("Optional explicit elbow hint. Blended in by bendGoalWeight.")]
        public Transform bendGoal;
        [Range(0f, 1f)] public float bendGoalWeight = 0f;
        [Tooltip("Degrees to rotate the elbow around the shoulder-to-hand axis")]
        public float swivelOffset = 0f;
        [Tooltip("Body-space elbow direction: x = outward, y = up, z = forward")]
        public Vector3 bendDirection = new Vector3(0.25f, -1f, -0.35f);
        [Tooltip("Scales the effective arm length, letting the hand reach further than the bones allow")]
        public float armLengthMlp = 1f;
    }

    [Serializable]
    public class Leg
    {
        [Tooltip("Optional explicit foot target (foot tracker). Overrides procedural stepping.")]
        public Transform target;
        [Range(0f, 1f)] public float positionWeight = 1f;
        [Range(0f, 1f)] public float rotationWeight = 1f;
        public Transform bendGoal;
        [Range(0f, 1f)] public float bendGoalWeight = 0f;
        public float swivelOffset = 0f;
        [Tooltip("Body-space knee direction: x = outward, y = up, z = forward")]
        public Vector3 bendDirection = new Vector3(0.1f, -0.2f, 1f);
        public float legLengthMlp = 1f;
    }

    [Serializable]
    public class Stepping
    {
        public LegMode mode = LegMode.PlantFeet;
        [Tooltip("Sideways distance between the feet, in metres")]
        public float footDistance = 0.25f;
        [Tooltip("A step starts once the foot is this far from where it should stand")]
        public float stepThreshold = 0.25f;
        [Min(0.01f)] public float stepDuration = 0.22f;
        public float stepHeight = 0.07f;
        [Tooltip("Raycast for the floor instead of using groundY")]
        public bool raycastGround = false;
        public LayerMask groundLayers = ~0;
        public float groundRayDistance = 2f;
        [Tooltip("Floor height along worldUp, used when raycastGround is off or the ray misses")]
        public float groundY = 0f;
    }

    [Serializable]
    public class Solver
    {
        [Range(0f, 1f), Tooltip("Master weight. 0 leaves the avatar in its bind pose.")]
        public float IKPositionWeight = 1f;
        public Spine spine = new Spine();
        public Arm leftArm = new Arm();
        public Arm rightArm = new Arm();
        public Leg leftLegIK = new Leg();
        public Leg rightLegIK = new Leg();
        public Stepping stepping = new Stepping();
    }

    [Tooltip("Restore the bind pose before each solve so results never compound frame over frame")]
    public bool fixTransforms = true;
    [Tooltip("World up axis used for the body heading and for grounding the feet")]
    public Vector3 worldUp = Vector3.up;

    public References references = new References();
    public Solver solver = new Solver();

    // ---------------- Runtime state ----------------

    struct FootState
    {
        public Vector3 planted;
        public Quaternion plantedRot;
        public Quaternion bindRotRelToBody;
        public bool stepping;
        public float t;
        public Vector3 from, to;
        public Quaternion fromRot, toRot;
    }

    bool _initiated;
    Vector3 _up = Vector3.up;

    Quaternion _bodyYaw = Quaternion.identity;
    Quaternion _bindBodyYaw = Quaternion.identity;

    Quaternion _pelvisRotRelToBody;
    Vector3 _pelvisOffsetFromHead;      // body space, pelvis relative to head in the bind pose
    Quaternion _headRotRelToBody;
    Quaternion _leftHandRotRelToBody;
    Quaternion _rightHandRotRelToBody;

    float _leftArmLength, _rightArmLength;
    float _leftLegLength, _rightLegLength;

    FootState _leftFootState, _rightFootState;

    Transform[] _bones;
    Vector3[] _bindLocalPos;
    Quaternion[] _bindLocalRot;

    // ---------------- Unity lifecycle ----------------

    void Start()
    {
        Initiate();
    }

    void LateUpdate()
    {
        if (!_initiated) return;

        if (fixTransforms) FixTransforms();

        float w = Mathf.Clamp01(solver.IKPositionWeight);
        if (w <= 0f) return;
        if (solver.spine.headTarget == null) return;

        float dt = Mathf.Max(Time.deltaTime, 1e-4f);

        UpdateBodyYaw(solver.spine.headTarget.rotation, dt);
        SolveSpineAndHead(solver.spine.headTarget.position, solver.spine.headTarget.rotation, w);

        SolveArm(references.leftShoulder, references.leftUpperArm, references.leftForearm, references.leftHand,
                 solver.leftArm, -1f, _leftArmLength, _leftHandRotRelToBody, w);
        SolveArm(references.rightShoulder, references.rightUpperArm, references.rightForearm, references.rightHand,
                 solver.rightArm, 1f, _rightArmLength, _rightHandRotRelToBody, w);

        SolveLeg(references.leftThigh, references.leftCalf, references.leftFoot,
                 ref _leftFootState, solver.leftLegIK, -1f, _leftLegLength, w, dt);
        SolveLeg(references.rightThigh, references.rightCalf, references.rightFoot,
                 ref _rightFootState, solver.rightLegIK, 1f, _rightLegLength, w, dt);
    }

    // ---------------- Initialisation ----------------

    /// <summary>
    /// Samples the avatar's current pose as the bind pose the solver works from. Call it again
    /// (Recalibrate) after anything rescales or re-poses the rig, e.g. AvatarTPoseCalibrator.
    /// </summary>
    public void Initiate()
    {
        _initiated = false;

        if (references.root == null) references.root = transform;

        string missing;
        if (!references.IsValid(out missing))
        {
            Debug.LogError("[SimpleVRIK] Reference '" + missing + "' is not assigned. Solver disabled.", this);
            return;
        }

        _up = worldUp.sqrMagnitude < 1e-6f ? Vector3.up : worldUp.normalized;

        StoreBindLocalState();

        _bindBodyYaw = YawRotation(references.root.rotation * Vector3.forward,
                                   references.root.rotation * Vector3.up);
        _bodyYaw = _bindBodyYaw;
        Quaternion invBind = Quaternion.Inverse(_bindBodyYaw);

        _pelvisRotRelToBody = invBind * references.pelvis.rotation;
        _pelvisOffsetFromHead = invBind * (references.pelvis.position - references.head.position);
        _headRotRelToBody = invBind * references.head.rotation;
        _leftHandRotRelToBody = invBind * references.leftHand.rotation;
        _rightHandRotRelToBody = invBind * references.rightHand.rotation;

        _leftArmLength = ChainLength(references.leftUpperArm, references.leftForearm, references.leftHand);
        _rightArmLength = ChainLength(references.rightUpperArm, references.rightForearm, references.rightHand);
        _leftLegLength = ChainLength(references.leftThigh, references.leftCalf, references.leftFoot);
        _rightLegLength = ChainLength(references.rightThigh, references.rightCalf, references.rightFoot);

        InitFoot(ref _leftFootState, references.leftFoot, invBind);
        InitFoot(ref _rightFootState, references.rightFoot, invBind);

        _initiated = true;
    }

    [ContextMenu("Recalibrate (sample current pose as bind pose)")]
    public void Recalibrate()
    {
        Initiate();
    }

    [ContextMenu("Auto-detect References From Animator")]
    void AutoDetectReferences()
    {
        var animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning("[SimpleVRIK] No Animator found to auto-detect references from.", this);
            return;
        }
        references.AutoDetect(animator);
    }

    void InitFoot(ref FootState st, Transform foot, Quaternion invBind)
    {
        st = default(FootState);
        st.bindRotRelToBody = Quaternion.identity;
        if (foot == null) return;
        st.bindRotRelToBody = invBind * foot.rotation;
        st.planted = foot.position;
        st.plantedRot = foot.rotation;
        st.to = st.planted;
        st.toRot = st.plantedRot;
    }

    static float ChainLength(Transform a, Transform b, Transform c)
    {
        if (a == null || b == null || c == null) return 0f;
        return Vector3.Distance(a.position, b.position) + Vector3.Distance(b.position, c.position);
    }

    void StoreBindLocalState()
    {
        var list = new List<Transform>();
        AddBone(list, references.pelvis);
        AddBone(list, references.spine);
        AddBone(list, references.chest);
        AddBone(list, references.neck);
        AddBone(list, references.head);
        AddBone(list, references.leftShoulder);
        AddBone(list, references.leftUpperArm);
        AddBone(list, references.leftForearm);
        AddBone(list, references.leftHand);
        AddBone(list, references.rightShoulder);
        AddBone(list, references.rightUpperArm);
        AddBone(list, references.rightForearm);
        AddBone(list, references.rightHand);
        AddBone(list, references.leftThigh);
        AddBone(list, references.leftCalf);
        AddBone(list, references.leftFoot);
        AddBone(list, references.leftToes);
        AddBone(list, references.rightThigh);
        AddBone(list, references.rightCalf);
        AddBone(list, references.rightFoot);
        AddBone(list, references.rightToes);

        _bones = list.ToArray();
        _bindLocalPos = new Vector3[_bones.Length];
        _bindLocalRot = new Quaternion[_bones.Length];
        for (int i = 0; i < _bones.Length; i++)
        {
            _bindLocalPos[i] = _bones[i].localPosition;
            _bindLocalRot[i] = _bones[i].localRotation;
        }
    }

    static void AddBone(List<Transform> list, Transform t)
    {
        if (t != null) list.Add(t);
    }

    /// <summary>Puts every driven bone back to the pose captured by <see cref="Initiate"/>.</summary>
    public void FixTransforms()
    {
        if (_bones == null) return;
        for (int i = 0; i < _bones.Length; i++)
        {
            if (_bones[i] == null) continue;
            _bones[i].localPosition = _bindLocalPos[i];
            _bones[i].localRotation = _bindLocalRot[i];
        }
    }

    // ---------------- Body ----------------

    /// <summary>Heading (rotation about <see cref="_up"/>) derived from a look direction.</summary>
    Quaternion YawRotation(Vector3 forward, Vector3 upRef)
    {
        Vector3 flat = Vector3.ProjectOnPlane(forward, _up);

        // Looking (almost) straight up or down: the forward axis carries no heading, so take it
        // from the up axis instead, which then points along the body.
        if (flat.sqrMagnitude < 1e-4f)
        {
            float sign = Vector3.Dot(forward, _up) > 0f ? -1f : 1f;
            flat = Vector3.ProjectOnPlane(upRef * sign, _up);
        }
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.ProjectOnPlane(Vector3.forward, _up);
        if (flat.sqrMagnitude < 1e-6f) flat = Vector3.ProjectOnPlane(Vector3.right, _up);

        return Quaternion.LookRotation(flat.normalized, _up);
    }

    void UpdateBodyYaw(Quaternion headRot, float dt)
    {
        Quaternion headYaw = YawRotation(headRot * Vector3.forward, headRot * Vector3.up);

        float stiffness = Mathf.Clamp01(solver.spine.bodyRotStiffness);
        if (stiffness > 0f)
            _bodyYaw = Quaternion.Slerp(_bodyYaw, headYaw, 1f - Mathf.Exp(-stiffness * 30f * dt));

        // Hard clamp: the body never lags the head by more than maxRootAngle.
        float angle = Quaternion.Angle(_bodyYaw, headYaw);
        float maxAngle = Mathf.Max(0f, solver.spine.maxRootAngle);
        if (angle > maxAngle && angle > 1e-4f)
            _bodyYaw = Quaternion.Slerp(_bodyYaw, headYaw, (angle - maxAngle) / angle);
    }

    void SolveSpineAndHead(Vector3 headPos, Quaternion headRot, float w)
    {
        var r = references;
        var s = solver.spine;

        // 1. Place the pelvis so the head lands on the target.
        Quaternion pelvisRot = _bodyYaw * _pelvisRotRelToBody;
        Vector3 pelvisPos = headPos + _bodyYaw * _pelvisOffsetFromHead;

        if (s.pelvisTarget != null)
        {
            if (s.pelvisPositionWeight > 0f)
                pelvisPos = Vector3.Lerp(pelvisPos, s.pelvisTarget.position, Mathf.Clamp01(s.pelvisPositionWeight));
            if (s.pelvisRotationWeight > 0f)
                pelvisRot = Quaternion.Slerp(pelvisRot, s.pelvisTarget.rotation, Mathf.Clamp01(s.pelvisRotationWeight));
        }

        r.pelvis.rotation = Quaternion.Slerp(r.pelvis.rotation, pelvisRot, w);
        r.pelvis.position = Vector3.Lerp(r.pelvis.position, pelvisPos, w);

        // 2. Bend the spine chain so the head reaches the target rotation.
        Quaternion desiredHeadRot = s.headRotationMode == RotationMode.UseTargetRotation
            ? headRot * Quaternion.Euler(s.headRotationOffset)
            : headRot * _headRotRelToBody;

        float rw = w * Mathf.Clamp01(s.rotationWeight);
        BendTowardHead(r.spine, desiredHeadRot, s.spineBend * rw);
        BendTowardHead(r.chest, desiredHeadRot, s.chestBend * rw);
        BendTowardHead(r.neck, desiredHeadRot, s.neckBend * rw);
        r.head.rotation = Quaternion.Slerp(r.head.rotation, desiredHeadRot, rw);

        // 3. The spine rotations moved the head off the target; shift the pelvis to put it back.
        float pw = w * Mathf.Clamp01(s.positionWeight);
        if (pw > 0f)
            r.pelvis.position += (headPos - r.head.position) * pw;
    }

    /// <summary>
    /// Rotates an ancestor of the head by a fraction of the delta the head still needs. The head
    /// travels with it, so each call re-reads the remaining delta.
    /// </summary>
    void BendTowardHead(Transform bone, Quaternion desiredHeadRot, float fraction)
    {
        if (bone == null || fraction <= 0f) return;
        Quaternion delta = desiredHeadRot * Quaternion.Inverse(references.head.rotation);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, delta, Mathf.Clamp01(fraction)) * bone.rotation;
    }

    // ---------------- Limbs ----------------

    void SolveArm(Transform shoulder, Transform upper, Transform fore, Transform hand,
                  Arm arm, float sideSign, float armLength, Quaternion handRotRelToBody, float w)
    {
        if (arm == null || arm.target == null || upper == null || fore == null || hand == null) return;

        float pw = w * Mathf.Clamp01(arm.positionWeight);
        if (pw > 0f)
        {
            Vector3 targetPos = Vector3.Lerp(hand.position, arm.target.position, pw);

            if (shoulder != null && arm.shoulderRotationWeight > 0f)
            {
                Quaternion delta = Quaternion.FromToRotation(hand.position - shoulder.position,
                                                             targetPos - shoulder.position);
                shoulder.rotation = Quaternion.Slerp(Quaternion.identity, delta,
                                                     Mathf.Clamp01(arm.shoulderRotationWeight)) * shoulder.rotation;
            }

            Vector3 bendDir = new Vector3(arm.bendDirection.x * sideSign, arm.bendDirection.y, arm.bendDirection.z);
            Vector3 hint = BendHint(upper.position, targetPos, bendDir, armLength,
                                    arm.bendGoal, arm.bendGoalWeight, arm.swivelOffset);
            SolveTwoBone(upper, fore, hand, targetPos, hint, arm.armLengthMlp);
        }

        float rw = w * Mathf.Clamp01(arm.rotationWeight);
        if (rw > 0f)
        {
            Quaternion desired = arm.handRotationMode == RotationMode.UseTargetRotation
                ? arm.target.rotation * Quaternion.Euler(arm.rotationOffset)
                : arm.target.rotation * handRotRelToBody;
            hand.rotation = Quaternion.Slerp(hand.rotation, desired, rw);
        }
    }

    void SolveLeg(Transform thigh, Transform calf, Transform foot, ref FootState st,
                  Leg leg, float sideSign, float legLength, float w, float dt)
    {
        if (thigh == null || calf == null || foot == null || leg == null) return;

        var step = solver.stepping;

        if (leg.target != null)
        {
            st.planted = leg.target.position;
            st.plantedRot = leg.target.rotation;
            st.stepping = false;
        }
        else if (step.mode == LegMode.PlantFeet)
        {
            Vector3 stance = StancePosition(sideSign);
            Quaternion stanceRot = _bodyYaw * st.bindRotRelToBody;

            if (!st.stepping && Vector3.Distance(st.planted, stance) > step.stepThreshold)
            {
                st.stepping = true;
                st.t = 0f;
                st.from = st.planted;
                st.fromRot = st.plantedRot;
            }

            if (st.stepping)
            {
                st.to = stance;
                st.toRot = stanceRot;
                st.t += dt / Mathf.Max(0.01f, step.stepDuration);

                if (st.t >= 1f)
                {
                    st.t = 1f;
                    st.stepping = false;
                    st.planted = st.to;
                    st.plantedRot = st.toRot;
                }
                else
                {
                    float e = Mathf.SmoothStep(0f, 1f, st.t);
                    st.planted = Vector3.Lerp(st.from, st.to, e) + _up * (Mathf.Sin(st.t * Mathf.PI) * step.stepHeight);
                    st.plantedRot = Quaternion.Slerp(st.fromRot, st.toRot, e);
                }
            }
        }
        else
        {
            return; // FollowPelvis: the legs just hang off the pelvis.
        }

        float pw = w * Mathf.Clamp01(leg.positionWeight);
        if (pw > 0f)
        {
            Vector3 targetPos = Vector3.Lerp(foot.position, st.planted, pw);
            Vector3 bendDir = new Vector3(leg.bendDirection.x * sideSign, leg.bendDirection.y, leg.bendDirection.z);
            Vector3 hint = BendHint(thigh.position, targetPos, bendDir, legLength,
                                    leg.bendGoal, leg.bendGoalWeight, leg.swivelOffset);
            SolveTwoBone(thigh, calf, foot, targetPos, hint, leg.legLengthMlp);
        }

        float rw = w * Mathf.Clamp01(leg.rotationWeight);
        if (rw > 0f)
            foot.rotation = Quaternion.Slerp(foot.rotation, st.plantedRot, rw);
    }

    /// <summary>Where a foot should stand: under the pelvis, offset sideways, dropped to the floor.</summary>
    Vector3 StancePosition(float sideSign)
    {
        var step = solver.stepping;
        Vector3 p = references.pelvis.position + _bodyYaw * Vector3.right * (sideSign * step.footDistance * 0.5f);

        if (step.raycastGround)
        {
            Vector3 origin = p + _up * (step.groundRayDistance * 0.5f);
            RaycastHit hit;
            if (Physics.Raycast(origin, -_up, out hit, step.groundRayDistance,
                                step.groundLayers, QueryTriggerInteraction.Ignore))
                return hit.point;
        }

        return p + _up * (step.groundY - Vector3.Dot(p, _up));
    }

    /// <summary>A point defining the plane the limb bends in.</summary>
    Vector3 BendHint(Vector3 rootPos, Vector3 targetPos, Vector3 bodySpaceDir, float length,
                     Transform bendGoal, float bendGoalWeight, float swivelDeg)
    {
        Vector3 dir = bodySpaceDir.sqrMagnitude < 1e-6f ? Vector3.forward : bodySpaceDir.normalized;
        Vector3 hint = rootPos + (_bodyYaw * dir) * Mathf.Max(0.01f, length);

        if (bendGoal != null && bendGoalWeight > 0f)
            hint = Vector3.Lerp(hint, bendGoal.position, Mathf.Clamp01(bendGoalWeight));

        if (Mathf.Abs(swivelDeg) > 1e-3f)
        {
            Vector3 axis = targetPos - rootPos;
            if (axis.sqrMagnitude > 1e-8f)
                hint = rootPos + Quaternion.AngleAxis(swivelDeg, axis.normalized) * (hint - rootPos);
        }

        return hint;
    }

    // ---------------- Analytic two-bone IK ----------------

    /// <summary>Interior angle opposite to <paramref name="oppLen"/> in a triangle.</summary>
    static float TriangleAngle(float oppLen, float len1, float len2)
    {
        float c = Mathf.Clamp((len1 * len1 + len2 * len2 - oppLen * oppLen) / (2f * len1 * len2), -1f, 1f);
        return Mathf.Acos(c);
    }

    /// <summary>
    /// Rotates <paramref name="upper"/> and <paramref name="mid"/> so <paramref name="end"/> reaches
    /// <paramref name="targetPos"/>, bending in the plane defined by <paramref name="hintPos"/>.
    /// </summary>
    static void SolveTwoBone(Transform upper, Transform mid, Transform end,
                             Vector3 targetPos, Vector3 hintPos, float lengthMlp)
    {
        Vector3 a = upper.position;
        Vector3 b = mid.position;
        Vector3 c = end.position;

        Vector3 ab = b - a;
        Vector3 bc = c - b;
        Vector3 ac = c - a;
        Vector3 at = targetPos - a;

        float mlp = Mathf.Max(0.01f, lengthMlp);
        float abLen = ab.magnitude * mlp;
        float bcLen = bc.magnitude * mlp;
        float acLen = ac.magnitude;
        if (abLen < 1e-5f || bcLen < 1e-5f || acLen < 1e-5f) return;
        if (at.sqrMagnitude < 1e-8f) return;

        // Clamp the reach just inside full extension so the triangle stays solvable.
        float atLen = Mathf.Clamp(at.magnitude, Mathf.Abs(abLen - bcLen) + 1e-4f, (abLen + bcLen) - 1e-4f);

        float oldAngle = TriangleAngle(acLen, abLen, bcLen);
        float newAngle = TriangleAngle(atLen, abLen, bcLen);

        // Bend plane: keep the current one, falling back to the hint when the limb is straight.
        Vector3 axis = Vector3.Cross(ab, bc);
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(hintPos - a, bc);
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(at, bc);
        if (axis.sqrMagnitude < 1e-8f) return;
        axis.Normalize();

        // 1. Open or close the middle joint to the angle the target distance requires.
        float half = 0.5f * (oldAngle - newAngle);
        float sin = Mathf.Sin(half);
        mid.rotation = new Quaternion(axis.x * sin, axis.y * sin, axis.z * sin, Mathf.Cos(half)) * mid.rotation;

        // 2. Swing the whole limb so the end lands on the target.
        ac = end.position - a;
        if (ac.sqrMagnitude < 1e-8f) return;
        upper.rotation = Quaternion.FromToRotation(ac, at) * upper.rotation;

        // 3. Twist around the limb axis so the bend plane faces the hint.
        ac = end.position - a;
        float acSqr = ac.sqrMagnitude;
        if (acSqr < 1e-8f) return;
        Vector3 acNorm = ac / Mathf.Sqrt(acSqr);

        Vector3 abNow = mid.position - a;
        Vector3 ah = hintPos - a;
        Vector3 abProj = abNow - acNorm * Vector3.Dot(abNow, acNorm);
        Vector3 ahProj = ah - acNorm * Vector3.Dot(ah, acNorm);

        if (abProj.sqrMagnitude > 1e-6f && ahProj.sqrMagnitude > 1e-6f)
            upper.rotation = Quaternion.FromToRotation(abProj, ahProj) * upper.rotation;
    }
}
