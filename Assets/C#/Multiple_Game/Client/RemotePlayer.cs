using System.Collections.Generic;
using NetGame.Protocol;
using UnityEngine;

namespace NetGame.Unity
{
    /// <summary>
    /// 远端玩家表现：快照缓冲 + 渲染延迟插值 + 速度外推兜底。
    /// 只操作 GameObject（且仅在主线程被 Tick），不接触网络。
    /// </summary>
    public sealed class RemotePlayer
    {
        public int PlayerId { get; }
        public string Username { get; }
        public GameObject GameObject { get; }

        private readonly playerMove _move;
        private readonly playerCamera _cameraControl;

        private readonly Queue<Snapshot> _buffer = new Queue<Snapshot>();
        private const int MaxSnapshots = 60;
        private const float InterpolationDelay = 0.1f;
        private const float LookLerp = 12f;

        private float _targetPitch;
        private float _smoothedPitch;

        private struct Snapshot
        {
            public double Time;
            public Vector3 Pos;
            public float Yaw;
            public Vector3 Vel;
        }

        public RemotePlayer(int playerId, string username, GameObject go, playerMove move, playerCamera cam)
        {
            PlayerId = playerId;
            Username = username;
            GameObject = go;
            _move = move;
            _cameraControl = cam;
            _smoothedPitch = cam != null ? cam.GetPitch() : 0f;
            _targetPitch = _smoothedPitch;
        }

        public void PushState(PlayerState s)
        {
            _buffer.Enqueue(new Snapshot
            {
                Time = Now(),
                Pos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z),
                Yaw = s.Yaw,
                Vel = new Vector3(s.VelX, s.VelY, s.VelZ)
            });
            while (_buffer.Count > MaxSnapshots) _buffer.Dequeue();

            if (_move != null)
            {
                _move.SetMoveInput(new Vector2(s.InputX, s.InputY));
                _move.SetIsRunning(s.IsRunning);
            }
            _targetPitch = s.Pitch;
        }

        /// <summary>每帧在主线程调用。</summary>
        public void Tick(float deltaTime)
        {
            RenderPosition();
            RenderLook(deltaTime);
        }

        private void RenderPosition()
        {
            if (_buffer.Count == 0) return;
            double renderTime = Now() - InterpolationDelay;

            if (renderTime < _buffer.Peek().Time)
            {
                // 快照不足：用最老快照 + 速度外推
                Snapshot oldest = _buffer.Peek();
                float s = (float)(oldest.Time - renderTime);
                GameObject.transform.position = oldest.Pos + oldest.Vel * s;
                SetBodyYaw(oldest.Yaw);
                return;
            }

            Snapshot prev = _buffer.Peek();
            Snapshot next = prev;
            while (_buffer.Count > 1)
            {
                Snapshot[] arr = _buffer.ToArray();
                if (arr[1].Time <= renderTime)
                {
                    prev = _buffer.Dequeue();
                    next = _buffer.Peek();
                }
                else
                {
                    break;
                }
            }
            if (_buffer.Count > 1)
            {
                Snapshot[] arr = _buffer.ToArray();
                next = arr[1];
            }

            double span = next.Time - prev.Time;
            float t = span > 1e-6 ? (float)((renderTime - prev.Time) / span) : 1f;
            t = Mathf.Clamp01(t);

            GameObject.transform.position = Vector3.Lerp(prev.Pos, next.Pos, t);
            SetBodyYaw(Mathf.LerpAngle(prev.Yaw, next.Yaw, t));
        }

        private void SetBodyYaw(float yaw)
        {
            Vector3 e = GameObject.transform.eulerAngles;
            GameObject.transform.eulerAngles = new Vector3(e.x, yaw, e.z);
        }

        private void RenderLook(float deltaTime)
        {
            _smoothedPitch = Mathf.Lerp(_smoothedPitch, _targetPitch, deltaTime * LookLerp);
            if (_cameraControl != null) _cameraControl.SetPitch(_smoothedPitch);
        }

        private static double Now() => Time.unscaledTimeAsDouble;
    }
}
