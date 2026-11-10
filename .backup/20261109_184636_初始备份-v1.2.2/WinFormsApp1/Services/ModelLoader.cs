using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PrinterUpperComputer.Model;

namespace PrinterUpperComputer.Services
{
    /// <summary>
    /// 3D 数模加载器（本程序自写，无 native 依赖）：
    ///   .stl —— 同时支持【二进制 STL】和【ASCII 文本 STL】
    ///   .obj —— 支持标准 Wavefront OBJ（v / f，f 支持 a、a/b、a//b、a/b/c 写法，自动三角化）
    /// 生产环境若要支持 STEP/IGES/3MF 等更多格式，可在此类里接入开源库 AssimpNet，
    /// 对上层（切片器）暴露的仍然是 Mesh，不需要改其他代码。
    /// </summary>
    public static class ModelLoader
    {
        /// <summary>按扩展名加载模型，统一入口。</summary>
        public static Mesh Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到模型文件：" + path);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            Mesh mesh;

            if (ext == ".stl") mesh = LoadStl(path);
            else if (ext == ".obj") mesh = LoadObj(path);
            else throw new NotSupportedException("暂只支持 STL / OBJ 格式，当前文件：" + ext);

            mesh.SourceFile = Path.GetFileName(path);
            return mesh;
        }

        #region STL 解析

        private static Mesh LoadStl(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            /*
             * 判断二进制还是 ASCII：
             * 二进制 STL 结构 = 80 字节文件头 + 4 字节三角形数量 + N×50 字节
             * （每个三角形：法向量12 + 三个顶点36 + 属性2）。
             * 若文件长度正好等于 84 + N*50，必为二进制；否则按 ASCII 文本解析。
             */
            if (bytes.Length >= 84)
            {
                uint count = BitConverter.ToUInt32(bytes, 80);
                if (84L + count * 50L == bytes.Length)
                    return LoadBinaryStl(bytes, count);
            }
            return LoadAsciiStl(path);
        }

        /// <summary>二进制 STL：直接按字节偏移读 float。</summary>
        private static Mesh LoadBinaryStl(byte[] bytes, uint count)
        {
            var mesh = new Mesh();

            for (int i = 0; i < count; i++)
            {
                int off = 84 + i * 50;

                Vec3 n = new Vec3(
                    BitConverter.ToSingle(bytes, off + 0),
                    BitConverter.ToSingle(bytes, off + 4),
                    BitConverter.ToSingle(bytes, off + 8));

                Vec3 a = new Vec3(
                    BitConverter.ToSingle(bytes, off + 12),
                    BitConverter.ToSingle(bytes, off + 16),
                    BitConverter.ToSingle(bytes, off + 20));
                Vec3 b = new Vec3(
                    BitConverter.ToSingle(bytes, off + 24),
                    BitConverter.ToSingle(bytes, off + 28),
                    BitConverter.ToSingle(bytes, off + 32));
                Vec3 c = new Vec3(
                    BitConverter.ToSingle(bytes, off + 36),
                    BitConverter.ToSingle(bytes, off + 40),
                    BitConverter.ToSingle(bytes, off + 44));

                mesh.AddTriangle(new Triangle(a, b, c, n));
            }
            return mesh;
        }

        /// <summary>ASCII STL：逐行找 "facet normal" 和 "vertex"。</summary>
        private static Mesh LoadAsciiStl(string path)
        {
            var mesh = new Mesh();
            var verts = new List<Vec3>();
            Vec3 normal = new Vec3(0, 0, 1);

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                string[] p = line.Split(new[] { ' ', '\t' },
                                        StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;

                // facet normal nx ny nz
                if (p[0].Equals("facet", StringComparison.OrdinalIgnoreCase) && p.Length >= 5)
                {
                    normal = new Vec3(ParseFloat(p[2]), ParseFloat(p[3]), ParseFloat(p[4]));
                }
                // vertex x y z
                else if (p[0].Equals("vertex", StringComparison.OrdinalIgnoreCase) && p.Length >= 4)
                {
                    verts.Add(new Vec3(ParseFloat(p[1]), ParseFloat(p[2]), ParseFloat(p[3])));
                }
                // endfacet：一个三角形读完，正常应有 3 个顶点
                else if (p[0].Equals("endfacet", StringComparison.OrdinalIgnoreCase))
                {
                    if (verts.Count >= 3)
                        mesh.AddTriangle(new Triangle(verts[0], verts[1], verts[2], normal));
                    verts.Clear();
                }
            }
            return mesh;
        }

        #endregion

        #region OBJ 解析

        private static Mesh LoadObj(string path)
        {
            var mesh = new Mesh();
            var vertices = new List<Vec3>();

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                string[] p = line.Split(new[] { ' ', '\t' },
                                        StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;

                // v x y z  —— 顶点
                if (p[0] == "v" && p.Length >= 4)
                {
                    vertices.Add(new Vec3(ParseFloat(p[1]), ParseFloat(p[2]), ParseFloat(p[3])));
                }
                // f v1 v2 v3 [v4...] —— 面（可能是四边形，用扇形拆成三角形）
                else if (p[0] == "f" && p.Length >= 4)
                {
                    var idx = new List<int>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        /*
                         * 面的写法可能是：  f 10       f 10/20      f 10//30      f 10/20/30
                         * 只取第一个斜杠前的顶点号；负号表示从文件末尾倒数。
                         */
                        string token = p[i];
                        int slash = token.IndexOf('/');
                        string num = slash >= 0 ? token.Substring(0, slash) : token;

                        if (!int.TryParse(num, NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out int vi))
                            continue;

                        if (vi < 0) vi = vertices.Count + vi + 1; // OBJ 负索引
                        idx.Add(vi - 1);                          // OBJ 从 1 开始，C# 从 0 开始
                    }

                    // 扇形三角化：(0,1,2)、(0,2,3)、(0,3,4)...
                    for (int i = 1; i + 1 < idx.Count; i++)
                    {
                        Vec3 a = vertices[idx[0]];
                        Vec3 b = vertices[idx[i]];
                        Vec3 c = vertices[idx[i + 1]];
                        mesh.AddTriangle(new Triangle(a, b, c, new Vec3(0, 0, 1)));
                    }
                }
            }
            return mesh;
        }

        #endregion

        /// <summary>所有浮点文本统一按“小数点”解析，避免中文系统逗号/点差异。</summary>
        private static float ParseFloat(string s)
        {
            return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
