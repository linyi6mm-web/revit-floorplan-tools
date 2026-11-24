using Autodesk.Revit.DB;

namespace RevitAutoAnnotate
{
    internal sealed class ViewFrame
    {
        public XYZ Origin { get; private set; }
        public XYZ Right { get; private set; }
        public XYZ Up { get; private set; }

        public ViewFrame(View view)
        {
            Origin = view.Origin;
            Right = view.RightDirection.Normalize();
            Up = view.UpDirection.Normalize();
        }

        public Point2 ToLocal(XYZ point)
        {
            XYZ delta = point - Origin;
            return new Point2(delta.DotProduct(Right), delta.DotProduct(Up));
        }

    }

    internal struct Point2
    {
        public double X;
        public double Y;

        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static Point2 operator -(Point2 a, Point2 b) { return new Point2(a.X - b.X, a.Y - b.Y); }
        public static Point2 operator *(Point2 a, double k) { return new Point2(a.X * k, a.Y * k); }
    }
}
