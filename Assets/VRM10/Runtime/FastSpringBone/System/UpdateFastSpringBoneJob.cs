using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UniVRM10.FastSpringBones.Blittables;
#if ENABLE_SPRINGBONE_BURST
using Unity.Burst;
#endif

namespace UniVRM10.FastSpringBones.System
{
#if ENABLE_SPRINGBONE_BURST
    [BurstCompile]
#endif
    public struct UpdateFastSpringBoneJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<BlittableSpring> Springs;
        [ReadOnly] public NativeArray<BlittableJoint> Joints;
        [ReadOnly] public NativeArray<BlittableCollider> Colliders;

        [NativeDisableParallelForRestriction] public NativeArray<BlittableLogic> Logics;
        [NativeDisableParallelForRestriction] public NativeArray<BlittableTransform> Transforms;

        public float DeltaTime;

        public void Execute(int index)
        {
            var spring = Springs[index];
            var colliderSpan = spring.colliderSpan;
            var logicSpan = spring.logicSpan;

            for (var logicIndex = logicSpan.startIndex; logicIndex < logicSpan.startIndex + logicSpan.count; ++logicIndex)
            {
                var logic = Logics[logicIndex];
                var joint = Joints[logicIndex];

                var headTransform = Transforms[logic.headTransformIndex];
                var parentTransform = logic.parentTransformIndex >= 0
                    ? Transforms[logic.parentTransformIndex]
                    : (BlittableTransform?)null;
                var centerTransform = spring.centerTransformIndex >= 0
                    ? Transforms[spring.centerTransformIndex]
                    : (BlittableTransform?)null;


                // 親があったら、親に依存するTransformを再計算
                if (parentTransform.HasValue)
                {
                    headTransform.position =
                        parentTransform.Value.localToWorldMatrix.MultiplyPoint3x4(headTransform.localPosition);
                    headTransform.rotation = parentTransform.Value.rotation * headTransform.localRotation;
                }

                var currentTail = centerTransform.HasValue
                    ? centerTransform.Value.localToWorldMatrix.MultiplyPoint3x4(logic.currentTail)
                    : logic.currentTail;
                var prevTail = centerTransform.HasValue
                    ? centerTransform.Value.localToWorldMatrix.MultiplyPoint3x4(logic.prevTail)
                    : logic.prevTail;

                var parentRotation = parentTransform?.rotation ?? Quaternion.identity;

                // verlet積分で次の位置を計算
                var external = joint.gravityDir * joint.gravityPower * DeltaTime;
                var nextTail = currentTail
                               + (currentTail - prevTail) * (1.0f - joint.dragForce) // 前フレームの移動を継続する(減衰もあるよ)
                               + parentRotation * logic.localRotation * logic.boneAxis *
                               joint.stiffnessForce * DeltaTime // 親の回転による子ボーンの移動目標
                               + external; // 外力による移動量

                // 長さをboneLengthに強制
                nextTail = headTransform.position + (nextTail - headTransform.position).normalized * logic.length;

                // Collisionで移動
                for (var colliderIndex = colliderSpan.startIndex; colliderIndex < colliderSpan.startIndex + colliderSpan.count; ++colliderIndex)
                {
                    var collider = Colliders[colliderIndex];
                    var colliderTransform = Transforms[collider.transformIndex];
                    var worldPosition = colliderTransform.localToWorldMatrix.MultiplyPoint3x4(collider.offset);
                    var worldTail = colliderTransform.localToWorldMatrix.MultiplyPoint3x4(collider.tail);
                    
                    switch (collider.colliderType)
                    {
                        case BlittableColliderType.Sphere:
                            ResolveSphereCollision(joint, collider,  worldPosition, headTransform, logic, ref nextTail);
                            break;
                        case BlittableColliderType.Capsule:
                            ResolveCapsuleCollision(worldTail, worldPosition, headTransform, joint, collider, logic, ref nextTail);
                            break;
                    }
                }

                logic.prevTail = centerTransform.HasValue
                    ? centerTransform.Value.worldToLocalMatrix.MultiplyPoint3x4(logic.currentTail)
                    : logic.currentTail;
                logic.currentTail = centerTransform.HasValue
                    ? centerTransform.Value.worldToLocalMatrix.MultiplyPoint3x4(nextTail)
                    : nextTail;


                //回転を適用
                var rotation = parentRotation * logic.localRotation;
                headTransform.rotation = Quaternion.FromToRotation(rotation * logic.boneAxis,
                    nextTail - headTransform.position) * rotation;

                // Transformを更新
                if (parentTransform.HasValue)
                {
                    var parentLocalToWorldMatrix = parentTransform.Value.localToWorldMatrix;
                    headTransform.localRotation = (Quaternion.Inverse(parentTransform.Value.rotation) * headTransform.rotation).normalized;
                    headTransform.localToWorldMatrix =
                        parentLocalToWorldMatrix *
                        Matrix4x4.TRS(
                            headTransform.localPosition,
                            headTransform.localRotation,
                            headTransform.localScale
                        );
                    headTransform.worldToLocalMatrix = headTransform.localToWorldMatrix.inverse;
                }
                else
                {
                    headTransform.localToWorldMatrix =
                        Matrix4x4.TRS(
                            headTransform.position,
                            headTransform.rotation,
                            headTransform.localScale
                        );
                    headTransform.worldToLocalMatrix = headTransform.localToWorldMatrix.inverse;
                    headTransform.localRotation = headTransform.rotation;
                }

                // 値をバッファに戻す
                Transforms[logic.headTransformIndex] = headTransform;
                Logics[logicIndex] = logic;
            }
        }

        private static void ResolveCapsuleCollision(
            Vector3 worldTail,
            Vector3 worldPosition,
            BlittableTransform headTransform,
            BlittableJoint joint,
            BlittableCollider collider,
            BlittableLogic logic,
            ref Vector3 nextTail)
        {
            var P = worldTail - worldPosition;
            var Q = headTransform.position - worldPosition;
            var dot = Vector3.Dot(P, Q);
            if (dot <= 0)
            {
                // head側半球の球判定
                ResolveSphereCollision(joint, collider, worldPosition, headTransform, logic, ref nextTail);
            }

            var t = dot / P.magnitude;
            if (t >= 1.0f)
            {
                // tail側半球の球判定
                ResolveSphereCollision(joint, collider, worldTail, headTransform, logic, ref nextTail);
            }

            // head-tail上の m_transform.position との最近点
            var p = worldPosition + P * t;
            ResolveSphereCollision(joint, collider, p, headTransform, logic, ref nextTail);
        }

        private static void ResolveSphereCollision(
            BlittableJoint joint,
            BlittableCollider collider,
            Vector3 worldPosition,
            BlittableTransform headTransform,
            BlittableLogic logic,
            ref Vector3 nextTail)
        {
            var r = joint.radius + collider.radius;
            if (Vector3.SqrMagnitude(nextTail - worldPosition) <= (r * r))
            {
                // ヒット。Colliderの半径方向に押し出す
                var normal = (nextTail - worldPosition).normalized;
                var posFromCollider = worldPosition + normal * (joint.radius + collider.radius);
                // 長さをboneLengthに強制
                nextTail = headTransform.position + (posFromCollider - headTransform.position).normalized * logic.length;
            }
        }
    }
}