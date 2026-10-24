// Monkey Ball's own ball physics, world tilt and follow camera, ported line by line from the
// Super Monkey Ball decompilation (github.com/camthesaxman/smb-decomp: ball.c, world.c, stcoli.c,
// camera.c). Deluxe runs the same ball code.
//
// Everything here is in Monkey Ball's space and units: y up, 1 unit = the ball's diameter, speeds in
// units per frame, one Step per 1/60 s. Angles are the game's 16-bit angles (65536 = a full turn),
// kept as short so they wrap the same way. BallMode converts to and from GTA's world.
using System;
using GTA.Math;

namespace MonkeyBallGta
{
    static class SmbMath
    {
        public static float Rad(int a) => a * (float)(Math.PI * 2.0 / 65536.0);
        public static short Atan2(double y, double x) => (short)(int)Math.Round(Math.Atan2(y, x) * 65536.0 / (Math.PI * 2.0));
        public static short Degrees(double d) => (short)(int)(d * 65536.0 / 360.0);

        public static Vector3 RotX(Vector3 v, int a)
        {
            float c = (float)Math.Cos(Rad(a)), s = (float)Math.Sin(Rad(a));
            return new Vector3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
        }

        public static Vector3 RotY(Vector3 v, int a)
        {
            float c = (float)Math.Cos(Rad(a)), s = (float)Math.Sin(Rad(a));
            return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
        }

        public static Vector3 RotZ(Vector3 v, int a)
        {
            float c = (float)Math.Cos(Rad(a)), s = (float)Math.Sin(Rad(a));
            return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }
    }

    // The ball (ball.c handle_ball_linear_kinematics, stcoli.c collide_ball_with_plane) and the stage
    // tilt it rolls on (world.c world_sub_input_main).
    class SmbBall
    {
        public const float Radius = 0.5f;
        public const float Gravity = 0.009799992f;  // units/frame^2 (ballPhysicsParams[0])
        public const float Restitution = 0.5f;
        public const float Friction = 0.01f;        // share of the along-surface speed lost per touching frame
        public const float DefaultMaxTilt = 26f;    // degrees (the game's 23, raised a little for GTA's long roads)
        public static float MaxTilt = DefaultMaxTilt;  // F8/F9 in game (BallMode.ChangeTilt)

        public Vector3 Pos, PrevPos, Vel;
        public short XRot, ZRot;                    // stage tilt
        public bool Touched;                        // collided this frame
        public float Hardest;                       // this frame's hardest hit: the most negative speed into a surface
        public Vector3 HardestNormal;               // and that surface's normal
        public float GravityScale = 1f;             // 0 and then -1 after the goal, when the ball floats and flies off
        bool touchedLastFrame;
        public int Frames;                          // frames since the ball started (the camera waits 60)

        // Stick in -1..1 (x right, y down, like the GameCube stick divided by 60). The tilt is relative
        // to the camera, then eased in by a fifth of the remaining difference each frame.
        public void Tilt(float stickX, float stickY, short camRotY)
        {
            stickX = Math.Max(-1f, Math.Min(1f, stickX));
            stickY = Math.Max(-1f, Math.Min(1f, stickY));
            int inpX = (int)(stickY * SmbMath.Degrees(MaxTilt));
            int inpZ = (int)(-stickX * SmbMath.Degrees(MaxTilt));
            Vector3 up = SmbMath.RotY(SmbMath.RotX(SmbMath.RotZ(new Vector3(0f, 1f, 0f), inpZ), inpX), camRotY);
            inpX = SmbMath.Atan2(up.Z, up.Y);
            inpZ = -SmbMath.Atan2(up.X, Math.Sqrt(up.Z * up.Z + up.Y * up.Y));
            short dx = (short)(inpX - XRot), dz = (short)(inpZ - ZRot);
            XRot = (short)(int)(XRot + dx * 0.2);
            ZRot = (short)(int)(ZRot + dz * 0.2);
        }

        // Gravity, tipped by the stage tilt, then move. Call collisions (CollidePlane) after this.
        public void Move(short camRotY)
        {
            PrevPos = Pos;
            touchedLastFrame = Touched;
            Touched = false;
            Hardest = 0f;
            Vector3 accel = new Vector3(0f, -Gravity * GravityScale, 0f);
            // Rolling back toward the camera on the ground, gravity is up to 35% weaker.
            float h = Vel.X * Vel.X + Vel.Z * Vel.Z;
            if (touchedLastFrame && h > 1.1920929e-7f)
            {
                float inv = 1f / (float)Math.Sqrt(h);
                float f = -(float)Math.Sin(SmbMath.Rad(camRotY)) * Vel.X * inv - (float)Math.Cos(SmbMath.Rad(camRotY)) * Vel.Z * inv;
                if (f < 0f)
                    accel *= 1f + 0.65f * f;
            }
            // Into stage space: the inverse of rotate_x(xrot) then rotate_z(zrot), i.e. undo x, then undo z.
            accel = SmbMath.RotZ(SmbMath.RotX(accel, -XRot), -ZRot);
            Vel += accel;
            Pos += Vel;
            Frames++;
        }

        // Move the ball out in front of a surface without touching its speed (for things it shoves).
        public void PushOut(Vector3 point, Vector3 normal)
        {
            float dist = Vector3.Dot(Pos - point, normal);
            if (dist < Radius)
                Pos += normal * (Radius - dist);
        }

        // One collision plane (a point on it and its unit normal pointing out toward the ball).
        public void CollidePlane(Vector3 point, Vector3 normal)
        {
            float dist = Vector3.Dot(Pos - point, normal);
            if (dist > Radius)
                return;
            Pos += normal * (Radius - dist);
            float into = Vector3.Dot(Vel, normal);
            if (into < Hardest)
            {
                Hardest = into;
                HardestNormal = normal;
            }
            if (into < 0f)
            {
                Vector3 along = Vel - normal * into;
                Vel -= along * Friction;
                if (into >= -5f * Gravity)
                {
                    Vel -= normal * into;
                }
                else
                {
                    float left = into + 5f * Gravity;
                    Vel -= normal * (-5f * Gravity);
                    Vel -= normal * ((1f + Restitution) * left);
                }
            }
            Touched = true;
        }
    }

    // The in-level follow camera (camera.c camera_func_level_main) and its view tilt (camera_main).
    class SmbCamera
    {
        public Vector3 Eye, LookAt;
        public short RotY, RotX;
        Vector3 boom;       // a point trailing behind the ball that the camera swings toward
        short pitch;        // smoothed (unkB8)
        short yawVel;       // last frame's turn (unk10C)
        short facing;       // the monkey's facing (ball unk92), stood in for by the travel direction

        public void Reset(SmbBall ball, short rotY)
        {
            RotY = rotY;
            facing = rotY;
            pitch = 0;
            yawVel = 0;
            LookAt = ball.Pos + new Vector3(0f, 0.5f, 0f);
            boom = LookAt + SmbMath.RotY(new Vector3(0f, 0f, 3f), RotY);
            Step(ball);
        }

        public void Step(SmbBall ball)
        {
            UpdateFacing(ball);
            Vector3 d = boom - LookAt;
            float len = d.Length();
            d = len > 1.1920929e-7f ? d / len : new Vector3(1f, 0f, 0f);
            Vector3 trail = d * 0.75f + LookAt;
            LookAt = ball.Pos + new Vector3(0f, 0.5f, 0f);
            trail = LookAt - trail;

            int targetPitch = ball.Frames < 60 ? 0 : SmbMath.Atan2(trail.Y, Math.Sqrt(trail.X * trail.X + trail.Z * trail.Z));
            short yaw = (short)(SmbMath.Atan2(trail.X, trail.Z) - 32768);
            int r3 = (short)(yaw - RotY);
            yaw = (short)(RotY + Clamp(r3, -512, 512));

            // Lean round toward where the monkey faces once it's more than 1/32 of a turn off.
            r3 = (short)(facing - yaw);
            if (r3 > 0x800) r3 -= 0x800;
            else if (r3 < -0x800) r3 += 0x800;
            else r3 = 0;
            r3 >>= 7;
            int r4 = yawVel;
            if (r3 == 0) r4 = 0;
            else if ((r4 < 0 && r3 > 0) || (r4 > 0 && r3 < 0)) r4 = 0;
            else if (r3 < 0) r4 = r3 < r4 - 4 ? r4 - 4 : r3;
            else r4 = r3 > r4 + 4 ? r4 + 4 : r3;
            yaw = (short)(yaw + r4);
            r3 = Clamp((short)(yaw - RotY), -768, 768);
            yaw = (short)(RotY + r3);

            targetPitch = Clamp(targetPitch, -6144, 6144);
            pitch = (short)(int)(pitch + 0.2 * (targetPitch - pitch));

            boom = LookAt + SmbMath.RotY(SmbMath.RotX(new Vector3(0f, 0f, 3f), pitch), yaw);
            yawVel = (short)(yaw - RotY);
            RotY = yaw;
            RotX = (short)(pitch + 62208);
            Eye = LookAt + SmbMath.RotY(SmbMath.RotX(new Vector3(0f, 0f, (float)Math.Sqrt(10.0)), RotX), RotY);
        }

        // ball.c func_8003CB88: below 0.23 units/frame keep the old facing, above 0.37 face the way the
        // ball is rolling, blend in between.
        void UpdateFacing(SmbBall ball)
        {
            float speed = (float)Math.Sqrt(ball.Vel.X * ball.Vel.X + ball.Vel.Z * ball.Vel.Z);
            float t = speed < 0.23148148f ? 0f : speed < 0.37037037f ? (speed - 0.23148148f) / 0.13888889f : 1f;
            short travel = (short)(SmbMath.Atan2(ball.Vel.X, ball.Vel.Z) - 32768);
            short diff = (short)(travel - facing);
            facing = (short)(facing + (int)(diff * t));
        }

        // The camera's forward and up after the view tilt: the game tips the whole view about the ball
        // by 0.6 of the stage tilt.
        public void Pose(SmbBall ball, out Vector3 eye, out Vector3 forward, out Vector3 up)
        {
            Vector3 Tip(Vector3 v) => SmbMath.RotZ(SmbMath.RotX(v, (int)(-ball.XRot * 0.6)), (int)(-ball.ZRot * 0.6));
            // camera rotation is rotate_y(rotY) then rotate_x(rotX); it looks down its -z
            forward = Tip(SmbMath.RotY(SmbMath.RotX(new Vector3(0f, 0f, -1f), RotX), RotY));
            up = Tip(SmbMath.RotY(SmbMath.RotX(new Vector3(0f, 1f, 0f), RotX), RotY));
            eye = ball.Pos + Tip(Eye - ball.Pos);
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
