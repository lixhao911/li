using System.Collections.Generic;

namespace PrinterUpperComputer.Model
{
    /// <summary>
    /// 三维向量 / 点（STL/OBJ 读出来的顶点就是它）。
    /// 用 float 是因为二进制 STL 中坐标本身就是 32 位单精度浮点数。
    /// </summary>
    public struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    /// <summary>
    /// 二维点（切片时，三角面与某一高度水平面相交后，投影到 XY 平面用）。
    /// </summary>
    public struct Vec2
    {
        public double X;
        public double Y;

        public Vec2(double x, double y) { X = x; Y = y; }
    }

    /// <summary>
    /// 三角形：3D 打印数模（STL）本质上就是一堆三角形的集合。
    /// A/B/C 为三个顶点，N 为法向量（切片本身用不到，仅解析时保留）。
    /// </summary>
    public struct Triangle
    {
        public Vec3 A;
        public Vec3 B;
        public Vec3 C;
        public Vec3 N;

        public Triangle(Vec3 a, Vec3 b, Vec3 c, Vec3 n)
        {
            A = a; B = b; C = c; N = n;
        }
    }

    /// <summary>
    /// 二维线段：某一层上，一个三角形与水平面交出的线段。
    /// 大量线段首尾相连后，就构成了该层的外轮廓（环）。
    /// </summary>
    public struct Segment2
    {
        public Vec2 A;
        public Vec2 B;
        public Segment2(Vec2 a, Vec2 b) { A = a; B = b; }
    }

    /// <summary>
    /// 网格模型：一个数模文件解析后的全部结果。
    /// </summary>
    public class Mesh
    {
        /// <summary>全部三角面</summary>
        public List<Triangle> Triangles { get; } = new List<Triangle>();

        /// <summary>包围盒最小值（模型最靠 X-/Y-/Z- 的点）</summary>
        public Vec3 Min { get; set; }

        /// <summary>包围盒最大值</summary>
        public Vec3 Max { get; set; }

        /// <summary>来源文件名，生成 G 代码注释时用</summary>
        public string SourceFile { get; set; }

        /// <summary>添加三角形并顺便维护包围盒（解析器每读到一个三角形就调一次）。</summary>
        public void AddTriangle(Triangle t)
        {
            if (Triangles.Count == 0)
            {
                Min = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
                Max = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            }

            Triangles.Add(t);

            Vec3[] pts = { t.A, t.B, t.C };
            foreach (Vec3 p in pts)
            {
                if (p.X < Min.X) Min = new Vec3(p.X, Min.Y, Min.Z);
                if (p.Y < Min.Y) Min = new Vec3(Min.X, p.Y, Min.Z);
                if (p.Z < Min.Z) Min = new Vec3(Min.X, Min.Y, p.Z);
                if (p.X > Max.X) Max = new Vec3(p.X, Max.Y, Max.Z);
                if (p.Y > Max.Y) Max = new Vec3(Max.X, p.Y, Max.Z);
                if (p.Z > Max.Z) Max = new Vec3(Max.X, Max.Y, p.Z);
            }
        }

        /// <summary>X / Y / Z 三个方向的尺寸（界面上显示给用户看）。</summary>
        public string SizeText
        {
            get
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.##} × {1:0.##} × {2:0.##} mm",
                    Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);
            }
        }
    }
}
