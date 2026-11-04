using AMUI;
using System;
using System.Text.Json.Serialization;
using System.Windows.Media.Media3D;

namespace T6_Workbench
{
    public class PreviewCameraState : BaseViewModel
    {
        double positionX;
        double positionY;
        double positionZ;
        double lookDirectionX;
        double lookDirectionY;
        double lookDirectionZ;

        public PreviewCameraState() { }

        public double PositionX { get => positionX; set => SetProperty(ref positionX, value); }
        public double PositionY { get => positionY; set => SetProperty(ref positionY, value); }
        public double PositionZ { get => positionZ; set => SetProperty(ref positionZ, value); }
        public double LookDirectionX { get => lookDirectionX; set => SetProperty(ref lookDirectionX, value); }
        public double LookDirectionY { get => lookDirectionY; set => SetProperty(ref lookDirectionY, value); }
        public double LookDirectionZ { get => lookDirectionZ; set => SetProperty(ref lookDirectionZ, value); }

        [JsonIgnore]
        public Point3D Position => new Point3D(PositionX, PositionY, PositionZ);
        [JsonIgnore]
        public Vector3D LookDirection => new Vector3D(LookDirectionX, LookDirectionY, LookDirectionZ);
        [JsonIgnore]
        public Vector3D UpDirection => new Vector3D(0, 1, 0);
        [JsonIgnore]
        public double FieldOfView => 45;
        [JsonIgnore]
        public bool HasPose
        {
            get
            {
                Vector3D look = LookDirection;
                Point3D target = Position + look;
                double distance = look.Length;
                return Helpers.IsFinite(PositionX) && Helpers.IsFinite(PositionY) && Helpers.IsFinite(PositionZ) &&
                    Helpers.IsFinite(LookDirectionX) && Helpers.IsFinite(LookDirectionY) && Helpers.IsFinite(LookDirectionZ) &&
                    Helpers.IsFinite(target.X) && Helpers.IsFinite(target.Y) && Helpers.IsFinite(target.Z) &&
                    Helpers.IsFinite(distance) && distance > 0.0001;
            }
        }

        internal void SetPose(Point3D position, Vector3D lookDirection)
        {
            PositionX = position.X;
            PositionY = position.Y;
            PositionZ = position.Z;
            LookDirectionX = lookDirection.X;
            LookDirectionY = lookDirection.Y;
            LookDirectionZ = lookDirection.Z;
            NotifyProperty(nameof(Position));
            NotifyProperty(nameof(LookDirection));
            NotifyProperty(nameof(HasPose));
        }

        internal void FitToBounds(Rect3D bounds)
        {
            var target = new Point3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            double distance = Math.Max(1, Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ)) * 2.4);
            const double yaw = 0.75;
            const double pitch = 0.35;
            double horizontal = distance * Math.Cos(pitch);
            var position = new Point3D(target.X + horizontal * Math.Cos(yaw),
                target.Y + distance * Math.Sin(pitch), target.Z + horizontal * Math.Sin(yaw));
            SetPose(position, target - position);
        }

        internal void Rotate(double deltaX, double deltaY)
        {
            Vector3D look = LookDirection;
            double distance = look.Length;
            if (!HasPose || distance <= 0.0001) 
                return;
            Point3D target = Position + look;
            double yaw = Math.Atan2(-look.Z, -look.X) + deltaX * 0.01;
            double pitch = Helpers.Clamp(Math.Asin(Helpers.Clamp(-look.Y / distance, -1, 1)) + deltaY * 0.01, -1.45, 1.45);
            double horizontal = distance * Math.Cos(pitch);
            var position = new Point3D(target.X + horizontal * Math.Cos(yaw),
                target.Y + distance * Math.Sin(pitch), target.Z + horizontal * Math.Sin(yaw));
            SetPose(position, target - position);
        }

        internal void Pan(double deltaX, double deltaY, double viewportHeight)
        {
            Vector3D look = LookDirection;
            double distance = look.Length;
            if (!HasPose || distance <= 0.0001) 
                return;
            Vector3D forward = look;
            forward.Normalize();
            var right = Vector3D.CrossProduct(forward, UpDirection);
            if (right.LengthSquared < 1e-10) 
                right = new Vector3D(1, 0, 0);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, forward);
            up.Normalize();
            double worldPerPixel = 2 * distance * Math.Tan(Math.PI / 8) / Math.Max(1, viewportHeight);
            Vector3D shift = right * (-deltaX * worldPerPixel) + up * (deltaY * worldPerPixel);
            SetPose(Position + shift, look);
        }

        internal void Zoom(bool towardTarget)
        {
            Vector3D look = LookDirection;
            double distance = look.Length;
            if (!HasPose || distance <= 0.0001) 
                return;
            double newDistance = Helpers.Clamp(distance * (towardTarget ? 0.85 : 1.18), 0.05, 1_000_000);
            Point3D target = Position + look;
            Vector3D newLook = look * (newDistance / distance);
            SetPose(target - newLook, newLook);
        }
    }
}
