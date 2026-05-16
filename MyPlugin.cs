#define DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using FurniturePlugin.Commands;
using Microsoft.Win32;

namespace FurniturePlugin;

public partial class MyPlugin : IExtensionApplication
{
    private List<Entity> _attachmentPreviewEntities = new();

    public struct SegmentKey2D { public Point2d A; public Point2d B; }

    private class BoundingRectangle

	{

		public double Width { get; set; }



		public double Height { get; set; }



		public double Area { get; set; }



		public double Angle { get; set; }

	}

    private class EigenDecompositionResult

	{

		public double[] EigenValues { get; set; }



		public double[,] EigenVectors { get; set; }

	}

    private struct BoundingRect2D
    {
        public double Angle { get; set; }

        public double MinX { get; set; }

        public double MaxX { get; set; }

        public double MinY { get; set; }

        public double MaxY { get; set; }
    }

    private class ProjectionPlane
    {
        public Vector3d Normal { get; set; }

        public Vector3d UpVector { get; set; }
    }

    private struct Vector2d
    {
        public double X { get; set; }

        public double Y { get; set; }

        public Vector2d(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    private class FaceInfo
    {
        public Vector3d Normal { get; set; }

        public double Area { get; set; }

        public List<Point3d> Vertices { get; set; }
    }

    public void Initialize()
    {
        try
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                var ed = doc.Editor;
                ed.WriteMessage("\n══════════════════════════════════════════");
                ed.WriteMessage("\n  FurniturePlugin v1.0 已加载");
                ed.WriteMessage("\n──────────────────────────────────────────");
                ed.WriteMessage("\n  排版类:");
                ed.WriteMessage("\n    PAIBAN     — 板件排样（渐进式小板优先）");
                ed.WriteMessage("\n  板件管理:");
                ed.WriteMessage("\n    SETI       — 设置板件属性（双击板件打开）");
                ed.WriteMessage("\n    CKBJ       — 查看板件信息列表");
                ed.WriteMessage("\n    PLXG       — 批量修改板件信息");
                ed.WriteMessage("\n    ZBBH       — 智能编号");
                ed.WriteMessage("\n    BOM        — 物料清单导出");
                ed.WriteMessage("\n  框架类:");
                ed.WriteMessage("\n    KJ         — 创建外框架");
                ed.WriteMessage("\n    WKK        — 搭积木建框架");
                ed.WriteMessage("\n    JBJ        — 框架添加板件");
                ed.WriteMessage("\n  工具类:");
                ed.WriteMessage("\n    OBB        — 有向包围盒计算");
                ed.WriteMessage("\n    BZBJ       — 板件摆正到XY平面");
                ed.WriteMessage("\n    FBGL       — 封边管理");
                ed.WriteMessage("\n    GICD       — 柜体自动拆单");
                ed.WriteMessage("\n    WJJL       — 五金件管理");
                ed.WriteMessage("\n  其他:");
                ed.WriteMessage("\n    ZJM        — 打开命令面板");
                ed.WriteMessage("\n    BOO        — 封闭曲线转实体");
                ed.WriteMessage("\n══════════════════════════════════════════\n");

                // ★ 双击板件打开SETI：拦截双击触发的 PROPERTIES 命令
                HookDoubleClickCommands();
            }
        }
        catch { }
        try { LoadConfiguration(); } catch { }
        try { System.Windows.Forms.Application.EnableVisualStyles(); } catch { }
    }

    private bool _doubleClickHooked;

    private void HookDoubleClickCommands()
    {
        if (_doubleClickHooked) return;
        try
        {
            foreach (Document doc in Application.DocumentManager)
            {
                doc.CommandWillStart += OnCommandWillStart;
            }
            Application.DocumentManager.DocumentCreated += (s, ev) =>
            {
                try { ev.Document.CommandWillStart += OnCommandWillStart; } catch { }
            };
            _doubleClickHooked = true;
        }
        catch { }
    }

    private void OnCommandWillStart(object sender, CommandEventArgs e)
    {
        try
        {
            string cmd = e?.GlobalCommandName;
            if (string.IsNullOrEmpty(cmd)) return;
            if (!string.Equals(cmd, "PROPERTIES", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cmd, "QUICKPROPERTIES", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cmd, "PROPERTIESCLOSE", StringComparison.OrdinalIgnoreCase))
                return;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var editor = doc.Editor;

            var implied = editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null || implied.Value.Count != 1)
                return;

            var objId = implied.Value[0].ObjectId;
            if (!Commands.SetInfoCommand.IsManagedPanelEntity(objId))
                return;

            doc.SendStringToExecute("SETI ", true, false, false);
        }
        catch { }
    }


    public void Terminate() { }

    private void LoadConfiguration() { }
    private static void RestorePreviousHighlight(Database db) { }
    private static PanelInfo GetPanelInfo(DBObject obj) => PanelInfoService.GetPanelInfo(obj, null);
    private static PanelInfo GetPanelInfo(DBObject obj, Transaction tr) => PanelInfoService.GetPanelInfo(obj, tr);
    private static void SetPanelInfoData(DBObject obj, PanelInfo info, Transaction tr) => PanelInfoService.SavePanelInfo((Entity)obj, info, tr);
    private static void SetPanelInfoFromBoxSizes(PanelInfo info, double sx, double sy, double sz) { info.Length = sx; info.Width = sy; info.Height = sz; }

    public static void CalculateAndSetDimensions(Solid3d solid, PanelInfo info) { CalculateAndSetDimensionsByType(solid, info); }

    /// <summary>
    /// 圆弧/样条导轨板件：与 SETI 同源截面轨对算法回填 L/W/T、裁切与展开相关字段。
    /// </summary>
    private static void ApplyFastDimensionForCurvedPanel(Solid3d solid, PanelInfo info)
    {
        if (solid == null || info == null) return;
        try
        {
            var dim = FastDimensionService.Compute(solid);
            if (dim == null || !dim.Success)
                return;

            info.Length = Math.Round(dim.Length, 2);
            info.Width = Math.Round(dim.Width, 2);
            info.Height = Math.Round(dim.Thickness, 2);
            if (dim.ArcInnerRadius > 0)
                info.ArcInnerRadius = Math.Round(dim.ArcInnerRadius, 2);
            if (dim.ArcAngleDegrees > 0)
                info.ArcAngleDegrees = Math.Round(dim.ArcAngleDegrees, 2);
            if (dim.ArcStraightLength1 > 0)
                info.ArcStraightLength1 = Math.Round(dim.ArcStraightLength1, 2);
            if (dim.ArcStraightLength2 > 0)
                info.ArcStraightLength2 = Math.Round(dim.ArcStraightLength2, 2);
            if (dim.UnfoldedLength > 0)
                info.UnfoldedLength = Math.Round(dim.UnfoldedLength, 2);
            if (dim.UnfoldedWidth > 0)
                info.UnfoldedWidth = Math.Round(dim.UnfoldedWidth, 2);
            if (dim.ArcOuterLength > 0)
                info.ArcOuterLength = Math.Round(dim.ArcOuterLength, 2);
            if (dim.ArcCenterLength > 0)
                info.ArcCenterLength = Math.Round(dim.ArcCenterLength, 2);
            if (dim.ArcInnerLength > 0)
                info.ArcInnerLengthValue = Math.Round(dim.ArcInnerLength, 2);
            if (!Enum.IsDefined(typeof(ArcLengthReferenceType), info.ArcLengthReference))
                info.ArcLengthReference = ArcLengthReferenceType.Outer;

            if (dim.DetectedType == PanelCalculationType.ArcPanel || dim.DetectedType == PanelCalculationType.SplinePanel)
            {
                info.Length = Math.Round(info.GetPreferredArcLength(), 2);
            }

            double e = 0.5, dl = 0, dw = 0;
            if (!string.IsNullOrEmpty(info.EdgeTop)) dl += e;
            if (!string.IsNullOrEmpty(info.EdgeBottom)) dl += e;
            if (!string.IsNullOrEmpty(info.EdgeLeft)) dw += e;
            if (!string.IsNullOrEmpty(info.EdgeRight)) dw += e;
            info.ExtraLength = Math.Round(info.Length - dl, 2);
            info.ExtraWidth = Math.Round(info.Width - dw, 2);
            info.ExtraHeight = Math.Round(info.Height, 2);
        }
        catch { /* FastDimensionService 设计上不抛异常，此处兜底 */ }
    }

    public static void CalculateAndSetDimensionsByType(Solid3d solid, PanelInfo info)
    {
        if (solid == null || info == null) return;
        if (info.CalculationType == PanelCalculationType.ArcPanel ||
            info.CalculationType == PanelCalculationType.SplinePanel)
        {
            ApplyFastDimensionForCurvedPanel(solid, info);
            return;
        }
        double length, width, thickness;
        switch (info.CalculationType)
        {
            case PanelCalculationType.AABB:
                CalcAABB(solid, out length, out width, out thickness);
                break;
            case PanelCalculationType.OBB:
                CalcOBBByMassProperties(solid, out length, out width, out thickness);
                break;
            case PanelCalculationType.Irregular:
                // 异形板：先按 OBB 拿到正确的厚度方向，再用 FLATSHOT 的真实 2D 包围盒覆盖长宽
                CalcOBBByMassProperties(solid, out length, out width, out thickness);
                TryRefineIrregularDimsByFlatshot(solid, ref length, ref width, ref thickness);
                break;
            default:
                CalcAABB(solid, out length, out width, out thickness);
                break;
        }
        info.Length = Math.Round(length, 2);
        info.Width = Math.Round(width, 2);
        info.Height = Math.Round(thickness, 2);
        double e = 0.5, dl = 0, dw = 0;
        if (!string.IsNullOrEmpty(info.EdgeTop)) dl += e;
        if (!string.IsNullOrEmpty(info.EdgeBottom)) dl += e;
        if (!string.IsNullOrEmpty(info.EdgeLeft)) dw += e;
        if (!string.IsNullOrEmpty(info.EdgeRight)) dw += e;
        info.ExtraLength = Math.Round(info.Length - dl, 2);
        info.ExtraWidth = Math.Round(info.Width - dw, 2);
        info.ExtraHeight = Math.Round(info.Height, 2);
    }
    public static void CalcOBBByMassProperties(Solid3d solid, out double length, out double width, out double thickness)
    {
        length = width = thickness = 0;
        try
        {
            double[] dims = new MyPlugin().CalculateOBBDimensionsFromSolid(solid);
            if (dims == null || dims.Length != 3)
            {
                CalcAABB(solid, out length, out width, out thickness);
                return;
            }

            Array.Sort(dims);
            thickness = Math.Round(dims[0], 2);
            width = Math.Round(dims[1], 2);
            length = Math.Round(dims[2], 2);
        }
        catch
        {
            CalcAABB(solid, out length, out width, out thickness);
        }
    }

    public static void CalcIrregularDimensions(Solid3d solid, out double length, out double width, out double thickness)
    {
        CalcOBBByMassProperties(solid, out length, out width, out thickness);
        TryRefineIrregularDimsByFlatshot(solid, ref length, ref width, ref thickness);
    }

    /// <summary>
    /// 异形板专用：在 OBB 给出的厚度方向基础上，
    /// 用 FLATSHOT 摆正后的真实 2D 包围盒覆盖长/宽，避免被 OBB 旋转误差放大。
    /// 失败则保持入参不变。
    /// </summary>
    private static void TryRefineIrregularDimsByFlatshot(
        Solid3d solid, ref double length, ref double width, ref double thickness, ObbPerformanceTrace trace = null)
    {
        try
        {
            // OutlineService.ExtractOutlineProfile 在 FLATSHOT 成功时会回填 TrueWidth/TrueHeight。
            OutlineService.OutlineProfile profile;
            if (trace == null)
            {
                profile = OutlineService.ExtractOutlineProfile(solid, length, width);
            }
            else
            {
                var sw = Stopwatch.StartNew();
                profile = OutlineService.ExtractOutlineProfile(solid, length, width);
                sw.Stop();
                trace.Add("flatshot.extract_outline", sw.Elapsed.TotalMilliseconds);
            }
            if (profile == null || !profile.FlatshotSuccess) return;
            if (profile.TrueWidth < 1.0 || profile.TrueHeight < 1.0) return;
            double L = Math.Max(profile.TrueWidth, profile.TrueHeight);
            double W = Math.Min(profile.TrueWidth, profile.TrueHeight);
            // 仅当 FLATSHOT 测得长宽小于 OBB（更紧实的真实尺寸）时采纳
            if (L > 0 && L <= length + 1.0)
                length = Math.Round(L, 2);
            if (W > 0 && W <= width + 1.0)
                width = Math.Round(W, 2);
            trace?.Add("flatshot.result", 0.0, $"{length:F2}x{width:F2}x{thickness:F2}");
        }
        catch { /* 任何失败都保留 OBB 结果 */ }
    }

private static bool TryGetThicknessNormalFromVertices(List<Point3d> vertices, out Vector3d normalAxis, out double thicknessRange, out double coverage)

	{

		normalAxis = Vector3d.ZAxis;

		thicknessRange = 0.0;

		coverage = 0.0;

		if (vertices == null || vertices.Count < 4)

		{

			return false;

		}

		Point3d val = vertices[0];

		double x = val.X;

		val = vertices[0];

		double x2 = val.X;

		val = vertices[0];

		double y = val.Y;

		val = vertices[0];

		double y2 = val.Y;

		val = vertices[0];

		double z = val.Z;

		val = vertices[0];

		double z2 = val.Z;

		for (int i = 1; i < vertices.Count; i++)

		{

			Point3d val2 = vertices[i];

			if (val2.X < x)

			{

				x = val2.X;

			}

			if (val2.X > x2)

			{

				x2 = val2.X;

			}

			if (val2.Y < y)

			{

				y = val2.Y;

			}

			if (val2.Y > y2)

			{

				y2 = val2.Y;

			}

			if (val2.Z < z)

			{

				z = val2.Z;

			}

			if (val2.Z > z2)

			{

				z2 = val2.Z;

			}

		}

		double num = x2 - x;

		double num2 = y2 - y;

		double num3 = z2 - z;

		double num4 = Math.Sqrt(num * num + num2 * num2 + num3 * num3);

		double num5 = Math.Max(0.0001, num4 * 0.0001);

		double num6 = double.MaxValue;

		Vector3d val3 = Vector3d.ZAxis;

		double num7 = 0.0;

		double num8 = 0.0;

		int count = vertices.Count;

		int num9 = 80;

		int num10 = 0;

		for (int j = 0; j < count; j++)

		{

			if (num10 >= num9)

			{

				break;

			}

			Point3d val4 = vertices[j];

			for (int k = j + 1; k < count; k++)

			{

				if (num10 >= num9)

				{

					break;

				}

				Point3d val5 = vertices[k];

				Vector3d val6 = val5 - val4;

				if (val6.Length <= 1E-09)

				{

					continue;

				}

				for (int l = k + 1; l < count; l++)

				{

					if (num10 >= num9)

					{

						break;

					}

					Point3d val7 = vertices[l];

					Vector3d val8 = val7 - val4;

					Vector3d val9 = val6.CrossProduct(val8);

					if (val9.Length <= 1E-09)

					{

						continue;

					}

					Vector3d normal = val9.GetNormal();

					double num11 = 0.0;

					double num12 = 0.0;

					bool flag = true;

					Vector3d asVector;

					for (int m = 0; m < count; m++)

					{

						val = vertices[m];

						asVector = val.GetAsVector();

						double num13 = asVector.DotProduct(normal);

						if (flag)

						{

							num11 = (num12 = num13);

							flag = false;

							continue;

						}

						if (num13 < num11)

						{

							num11 = num13;

						}

						if (num13 > num12)

						{

							num12 = num13;

						}

					}

					double num14 = num12 - num11;

					if (num14 <= 1E-09)

					{

						continue;

					}

					int num15 = 0;

					int num16 = 0;

					for (int n = 0; n < count; n++)

					{

						val = vertices[n];

						asVector = val.GetAsVector();

						double num17 = asVector.DotProduct(normal);

						if (Math.Abs(num17 - num11) <= num5)

						{

							num15++;

						}

						if (Math.Abs(num17 - num12) <= num5)

						{

							num16++;

						}

					}

					double num18 = (double)(num15 + num16) / (double)count;

					if (!(num18 < 0.7))

					{

						double num19 = num14 / num18;

						if (num19 < num6)

						{

							num6 = num19;

							val3 = normal;

							num7 = num14;

							num8 = num18;

						}

						num10++;

					}

				}

			}

		}

		if (num6 == double.MaxValue)

		{

			return false;

		}

		normalAxis = val3;

		thicknessRange = num7;

		coverage = num8;

		return true;

	}



private static bool TryGetProjectedExtents(List<Point3d> vertices, Vector3d axisX, Vector3d axisY, Vector3d axisZ, out double minX, out double maxX, out double minY, out double maxY, out double minZ, out double maxZ)

	{

		minX = (maxX = (minY = (maxY = (minZ = (maxZ = 0.0)))));

		if (vertices == null || vertices.Count == 0)

		{

			return false;

		}

		Vector3d normal = axisX.GetNormal();

		Vector3d normal2 = axisY.GetNormal();

		Vector3d normal3 = axisZ.GetNormal();

		bool flag = true;

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			Vector3d asVector = current.GetAsVector();

			double num = asVector.DotProduct(normal);

			double num2 = asVector.DotProduct(normal2);

			double num3 = asVector.DotProduct(normal3);

			if (flag)

			{

				minX = (maxX = num);

				minY = (maxY = num2);

				minZ = (maxZ = num3);

				flag = false;

				continue;

			}

			if (num < minX)

			{

				minX = num;

			}

			if (num > maxX)

			{

				maxX = num;

			}

			if (num2 < minY)

			{

				minY = num2;

			}

			if (num2 > maxY)

			{

				maxY = num2;

			}

			if (num3 < minZ)

			{

				minZ = num3;

			}

			if (num3 > maxZ)

			{

				maxZ = num3;

			}

		}

		return !flag;

	}



private static bool TryGetOrientedBoxFrame(List<Point3d> vertices, out Vector3d axisX, out Vector3d axisY, out Vector3d axisZ, out double minX, out double maxX, out double minY, out double maxY, out double minZ, out double maxZ)

	{

		axisX = Vector3d.XAxis;

		axisY = Vector3d.YAxis;

		axisZ = Vector3d.ZAxis;

		minX = (maxX = (minY = (maxY = (minZ = (maxZ = 0.0)))));

		if (vertices == null || vertices.Count < 4)

		{

			return false;

		}

		List<double> list = new List<double>(vertices.Count * vertices.Count);

		double num = 1E-06;

		Point3d val;

		for (int i = 0; i < vertices.Count; i++)

		{

			for (int j = i + 1; j < vertices.Count; j++)

			{

				val = vertices[i];

				double num2 = val.DistanceTo(vertices[j]);

				if (num2 > num)

				{

					list.Add(num2);

				}

			}

		}

		if (list.Count == 0)

		{

			return false;

		}

		list.Sort();

		List<double> list2 = new List<double>(3);

		foreach (double item in list)

		{

			double num3 = Math.Max(0.001, item * 0.0001);

			if (list2.Count == 0 || Math.Abs(item - list2[list2.Count - 1]) > num3)

			{

				list2.Add(item);

				if (list2.Count == 3)

				{

					break;

				}

			}

		}

		if (list2.Count < 2)

		{

			return false;

		}

		List<Vector3d> list3 = new List<Vector3d>(64);

		foreach (double item2 in list2)

		{

			double num4 = Math.Max(0.01, item2 * 0.001);

			for (int k = 0; k < vertices.Count; k++)

			{

				for (int l = k + 1; l < vertices.Count; l++)

				{

					val = vertices[k];

					double num5 = val.DistanceTo(vertices[l]);

					if (!(Math.Abs(num5 - item2) > num4))

					{

						Vector3d val2 = vertices[l] - vertices[k];

						if (!(val2.Length <= num))

						{

							list3.Add(val2.GetNormal());

						}

					}

				}

			}

		}

		if (list3.Count == 0)

		{

			return false;

		}

		List<Vector3d> list4 = new List<Vector3d>(3);

		foreach (Vector3d item3 in list3)

		{

			Vector3d current3 = item3;

			Vector3d normal = current3.GetNormal();

			bool flag = false;

			foreach (Vector3d item4 in list4)

			{

				if (Math.Abs(normal.DotProduct(item4)) > 0.99)

				{

					flag = true;

					break;

				}

			}

			if (!flag)

			{

				list4.Add(normal);

				if (list4.Count == 3)

				{

					break;

				}

			}

		}

		if (list4.Count < 2)

		{

			return false;

		}

		Vector3d val3 = list4[0];

		axisX = val3.GetNormal();

		int num6 = -1;

		double num7 = double.MaxValue;

		for (int m = 1; m < list4.Count; m++)

		{

			double num8 = Math.Abs(axisX.DotProduct(list4[m]));

			if (num8 < num7)

			{

				num7 = num8;

				num6 = m;

			}

		}

		if (num6 < 0)

		{

			return false;

		}

		val3 = list4[num6];

		axisY = val3.GetNormal();

		Vector3d val4 = axisX.CrossProduct(axisY);

		if (val4.Length <= num)

		{

			return false;

		}

		axisZ = val4.GetNormal();

		val3 = axisZ.CrossProduct(axisX);

		axisY = val3.GetNormal();

		if (list4.Count >= 3)

		{

			val3 = list4[2];

			Vector3d normal2 = val3.GetNormal();

			if (axisZ.DotProduct(normal2) < 0.0)

			{

				axisZ = axisZ.Negate();

				val3 = axisZ.CrossProduct(axisX);

				axisY = val3.GetNormal();

			}

		}

		bool flag2 = true;

		foreach (Point3d vertex in vertices)

		{

			Point3d current5 = vertex;

			Vector3d asVector = current5.GetAsVector();

			double num9 = asVector.DotProduct(axisX);

			double num10 = asVector.DotProduct(axisY);

			double num11 = asVector.DotProduct(axisZ);

			if (flag2)

			{

				minX = (maxX = num9);

				minY = (maxY = num10);

				minZ = (maxZ = num11);

				flag2 = false;

				continue;

			}

			if (num9 < minX)

			{

				minX = num9;

			}

			if (num9 > maxX)

			{

				maxX = num9;

			}

			if (num10 < minY)

			{

				minY = num10;

			}

			if (num10 > maxY)

			{

				maxY = num10;

			}

			if (num11 < minZ)

			{

				minZ = num11;

			}

			if (num11 > maxZ)

			{

				maxZ = num11;

			}

		}

		return true;

	}



private double[] CalculateOBBDimensionsFromSolid(Solid3d solid)

	{

		return CalculateOBBDimensionsFromSolid(solid, null);

	}

	private double[] CalculateOBBDimensionsFromSolid(Solid3d solid, ObbPerformanceTrace trace)

	{

		try

		{

			if (ObbComputationService.TryCompute(solid, trace, out var fastResult) && fastResult.Success)

			{

				trace?.Add("result.fast_path", 0.0, fastResult.Method);

				return new double[3]

				{

					fastResult.Length,

					fastResult.Width,

					fastResult.Thickness

				};

			}

			List<Point3d> realVerticesFromSolid = trace == null

				? GetRealVerticesFromSolid(solid)

				: trace.Measure("fallback.get_real_vertices", () => GetRealVerticesFromSolid(solid));

			if (realVerticesFromSolid != null && realVerticesFromSolid.Count >= 4)

			{

				double[] array = trace == null

					? CalculateOBBFromRealVertices(realVerticesFromSolid)

					: trace.Measure("fallback.obb_from_vertices", () => CalculateOBBFromRealVertices(realVerticesFromSolid), $"points={realVerticesFromSolid.Count}");

				if (array != null && ValidateOBBResult(array))

				{

					trace?.Add("result.fallback_vertices", 0.0, $"{array[0]:F2}x{array[1]:F2}x{array[2]:F2}");

					return array;

				}

			}

			double[] array2 = trace == null

				? CalculateOBBFromMajorFaces(solid)

				: trace.Measure("fallback.major_faces", () => CalculateOBBFromMajorFaces(solid));

			if (array2 != null && ValidateOBBResult(array2))

			{

				trace?.Add("result.major_faces", 0.0, $"{array2[0]:F2}x{array2[1]:F2}x{array2[2]:F2}");

				return array2;

			}

			double[] array3 = trace == null

				? CalculateAABBDimensions(solid)

				: trace.Measure("fallback.aabb", () => CalculateAABBDimensions(solid));

			trace?.Add("result.aabb", 0.0, $"{array3[0]:F2}x{array3[1]:F2}x{array3[2]:F2}");

			return array3;

		}

		catch (Exception ex)

		{

			Debug.WriteLine("OBB璁＄畻寮傚父: " + ex.Message);

			double[] array4 = trace == null

				? CalculateAABBDimensions(solid)

				: trace.Measure("fallback.aabb_on_exception", () => CalculateAABBDimensions(solid), ex.Message);

			trace?.Add("result.exception_fallback", 0.0, $"{array4[0]:F2}x{array4[1]:F2}x{array4[2]:F2}");

			return array4;

		}

	}



	private List<Point3d> GetRealVerticesFromSolid(Solid3d solid)

	{

		List<Point3d> vertices = new List<Point3d>();

		DBObjectCollection val = new DBObjectCollection();

		try

		{

			((Entity)solid).Explode(val);

			foreach (DBObject item in val)

			{

				DBObject val2 = item;

				Region val3 = (Region)(object)((val2 is Region) ? val2 : null);

				if (val3 != null)

				{

					DBObjectCollection val4 = new DBObjectCollection();

					((Entity)val3).Explode(val4);

					foreach (DBObject item2 in val4)

					{

						DBObject val5 = item2;

						ExtractVerticesFromCurve(val5, vertices);

						((DisposableWrapper)val5).Dispose();

					}

					((DisposableWrapper)val4).Dispose();

				}

				else

				{

					ExtractVerticesFromCurve(val2, vertices);

				}

				((DisposableWrapper)val2).Dispose();

			}

			return RemoveDuplicateVertices(vertices);

		}

		catch (Exception ex)

		{

			Debug.WriteLine("获取真实顶点失败: " + ex.Message);

			return null;

		}

		finally

		{

			((DisposableWrapper)val).Dispose();

		}

	}



	private void ExtractVerticesFromCurve(DBObject obj, List<Point3d> vertices)

	{

		Polyline val = (Polyline)(object)((obj is Polyline) ? obj : null);

		if (val != null)

		{

			int numberOfVertices = val.NumberOfVertices;

			for (int i = 0; i < numberOfVertices; i++)

			{

				vertices.Add(val.GetPoint3dAt(i));

			}

			return;

		}

		Curve val2 = (Curve)(object)((obj is Curve) ? obj : null);

		if (val2 == null)

		{

			return;

		}

		vertices.Add(val2.StartPoint);

		vertices.Add(val2.EndPoint);

		double num = 0.0;

		double num2 = 0.0;

		double num3 = 0.0;

		try

		{

			num = val2.GetDistanceAtParameter(val2.StartParam);

			num2 = val2.GetDistanceAtParameter(val2.EndParam);

			num3 = Math.Abs(num2 - num);

		}

		catch

		{

			num3 = 0.0;

		}

		if (!(num3 > 1E-06))

		{

			return;

		}

		int num4 = Math.Max(5, Math.Min(33, (int)(num3 / 25.0) + 5));

		for (int j = 1; j < num4 - 1; j++)

		{

			double num5 = (double)j / (double)(num4 - 1);

			double num6 = num + (num2 - num) * num5;

			try

			{

				vertices.Add(val2.GetPointAtDist(num6));

			}

			catch

			{

				try

				{

					double num7 = val2.StartParam + (val2.EndParam - val2.StartParam) * num5;

					vertices.Add(val2.GetPointAtParameter(num7));

				}

				catch

				{

				}

			}

		}

	}



	private List<Point3d> SampleSolidSurfaceDense(Solid3d solid)

	{

		List<Point3d> list = new List<Point3d>();

		Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

		Point3d minPoint = geometricExtents.MinPoint;

		Point3d maxPoint = geometricExtents.MaxPoint;

		double num = maxPoint.X - minPoint.X;

		double num2 = maxPoint.Y - minPoint.Y;

		double num3 = maxPoint.Z - minPoint.Z;

		double num4 = Math.Max(Math.Max(num, num2), num3);

		int num5 = Math.Max(8, Math.Min(20, (int)(num4 / 10.0) + 5));

		double num6 = num / (double)(num5 - 1);

		double num7 = num2 / (double)(num5 - 1);

		double num8 = num3 / (double)(num5 - 1);

		for (int i = 0; i < num5; i++)

		{

			for (int j = 0; j < num5; j++)

			{

				double num9 = minPoint.X + (double)i * num6;

				double num10 = minPoint.Y + (double)j * num7;

				double num11 = minPoint.Z + (double)j * num8;

				list.Add(new Point3d(num9, num10, minPoint.Z));

				list.Add(new Point3d(num9, num10, maxPoint.Z));

				list.Add(new Point3d(num9, minPoint.Y, num11));

				list.Add(new Point3d(num9, maxPoint.Y, num11));

				list.Add(new Point3d(minPoint.X, num10, num11));

				list.Add(new Point3d(maxPoint.X, num10, num11));

			}

		}

		AddEdgeAndCornerSamples(list, minPoint, maxPoint, num5 / 2);

		return list;

	}



	private void AddEdgeAndCornerSamples(List<Point3d> points, Point3d min, Point3d max, int edgeSamples)

	{

		for (int i = 0; i <= edgeSamples; i++)

		{

			double num = (double)i / (double)edgeSamples;

			points.Add(new Point3d(min.X + num * (max.X - min.X), min.Y, min.Z));

			points.Add(new Point3d(min.X + num * (max.X - min.X), max.Y, min.Z));

			points.Add(new Point3d(min.X + num * (max.X - min.X), min.Y, max.Z));

			points.Add(new Point3d(min.X + num * (max.X - min.X), max.Y, max.Z));

			points.Add(new Point3d(min.X, min.Y + num * (max.Y - min.Y), min.Z));

			points.Add(new Point3d(max.X, min.Y + num * (max.Y - min.Y), min.Z));

			points.Add(new Point3d(min.X, min.Y + num * (max.Y - min.Y), max.Z));

			points.Add(new Point3d(max.X, min.Y + num * (max.Y - min.Y), max.Z));

			points.Add(new Point3d(min.X, min.Y, min.Z + num * (max.Z - min.Z)));

			points.Add(new Point3d(max.X, min.Y, min.Z + num * (max.Z - min.Z)));

			points.Add(new Point3d(min.X, max.Y, min.Z + num * (max.Z - min.Z)));

			points.Add(new Point3d(max.X, max.Y, min.Z + num * (max.Z - min.Z)));

		}

	}



	private double[] CalculateOBBFromMajorFaces(Solid3d solid)

	{

		try

		{

			Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

			Point3d minPoint = geometricExtents.MinPoint;

			Point3d maxPoint = geometricExtents.MaxPoint;

			double num = Math.Abs(maxPoint.X - minPoint.X);

			double num2 = Math.Abs(maxPoint.Y - minPoint.Y);

			double num3 = Math.Abs(maxPoint.Z - minPoint.Z);

			double num4 = num * num2;

			double num5 = num * num3;

			double num6 = num2 * num3;

			Vector3d primaryNormal = ((num4 >= num5 && num4 >= num6) ? Vector3d.ZAxis : ((!(num5 >= num6)) ? Vector3d.XAxis : Vector3d.YAxis));

			double num7 = DetectRotationAngle(solid, primaryNormal);

			if (Math.Abs(num7) > 0.1)

			{

				return CalculateRotatedOBB(solid, primaryNormal, num7);

			}

			double[] array = new double[3] { num, num2, num3 };

			Array.Sort(array);

			Array.Reverse(array);

			return array;

		}

		catch (Exception ex)

		{

			Debug.WriteLine("主面OBB计算失败: " + ex.Message);

			return null;

		}

	}



	private double[] CalculateOBBFromImprovedSampling(Solid3d solid)

	{

		try

		{

			List<Point3d> boundingBoxVertices = GetBoundingBoxVertices(solid);

			List<Point3d> collection = SampleSolidSurfaceImproved(solid);

			boundingBoxVertices.AddRange(collection);

			boundingBoxVertices = RemoveDuplicateVertices(boundingBoxVertices);

			if (boundingBoxVertices.Count >= 8)

			{

				return CalculateOBBFromRealVertices(boundingBoxVertices);

			}

			return null;

		}

		catch (Exception ex)

		{

			Debug.WriteLine("鏀硅繘閲囨牱OBB璁＄畻澶辫触: " + ex.Message);

			return null;

		}

	}



	private List<Point3d> GetBoundingBoxVertices(Solid3d solid)

	{

		List<Point3d> list = new List<Point3d>();

		Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

		Point3d minPoint = geometricExtents.MinPoint;

		Point3d maxPoint = geometricExtents.MaxPoint;

		list.Add(new Point3d(minPoint.X, minPoint.Y, minPoint.Z));

		list.Add(new Point3d(maxPoint.X, minPoint.Y, minPoint.Z));

		list.Add(new Point3d(maxPoint.X, maxPoint.Y, minPoint.Z));

		list.Add(new Point3d(minPoint.X, maxPoint.Y, minPoint.Z));

		list.Add(new Point3d(minPoint.X, minPoint.Y, maxPoint.Z));

		list.Add(new Point3d(maxPoint.X, minPoint.Y, maxPoint.Z));

		list.Add(new Point3d(maxPoint.X, maxPoint.Y, maxPoint.Z));

		list.Add(new Point3d(minPoint.X, maxPoint.Y, maxPoint.Z));

		return list;

	}



	private List<Point3d> SampleSolidSurfaceImproved(Solid3d solid)

	{

		List<Point3d> list = new List<Point3d>();

		Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

		Point3d minPoint = geometricExtents.MinPoint;

		Point3d maxPoint = geometricExtents.MaxPoint;

		double num = (maxPoint.X - minPoint.X) / 4.0;

		double num2 = (maxPoint.Y - minPoint.Y) / 4.0;

		double num3 = (maxPoint.Z - minPoint.Z) / 4.0;

		for (int i = 1; i < 4; i++)

		{

			for (int j = 1; j < 4; j++)

			{

				list.Add(new Point3d(minPoint.X, minPoint.Y + (double)i * num2, minPoint.Z + (double)j * num3));

				list.Add(new Point3d(maxPoint.X, minPoint.Y + (double)i * num2, minPoint.Z + (double)j * num3));

			}

		}

		for (int k = 1; k < 4; k++)

		{

			for (int l = 1; l < 4; l++)

			{

				list.Add(new Point3d(minPoint.X + (double)k * num, minPoint.Y, minPoint.Z + (double)l * num3));

				list.Add(new Point3d(minPoint.X + (double)k * num, maxPoint.Y, minPoint.Z + (double)l * num3));

			}

		}

		for (int m = 1; m < 4; m++)

		{

			for (int n = 1; n < 4; n++)

			{

				list.Add(new Point3d(minPoint.X + (double)m * num, minPoint.Y + (double)n * num2, minPoint.Z));

				list.Add(new Point3d(minPoint.X + (double)m * num, minPoint.Y + (double)n * num2, maxPoint.Z));

			}

		}

		return list;

	}



	private double DetectRotationAngle(Solid3d solid, Vector3d primaryNormal)

	{

		try

		{

			return 0.0;

		}

		catch

		{

			return 0.0;

		}

	}



	private double[] CalculateRotatedOBB(Solid3d solid, Vector3d primaryNormal, double rotationAngle)

	{

		try

		{

			List<Point3d> boundingBoxVertices = GetBoundingBoxVertices(solid);

			Point3d center = CalculateCentroid(boundingBoxVertices);

			List<Point3d> vertices = RotateVerticesAroundAxis(boundingBoxVertices, center, primaryNormal, rotationAngle);

			return CalculateAABBFromVertices(vertices);

		}

		catch

		{

			return null;

		}

	}



	private List<Point3d> RotateVerticesAroundAxis(List<Point3d> vertices, Point3d center, Vector3d axis, double angle)

	{

		List<Point3d> list = new List<Point3d>();

		foreach (Point3d vertex in vertices)

		{

			Point3d val = vertex - center.GetAsVector();

			Vector3d val2 = RotateVectorAroundAxis(val.GetAsVector(), axis, angle);

			list.Add(center + val2);

		}

		return list;

	}



	private Vector3d RotateVectorAroundAxis(Vector3d vector, Vector3d axis, double angle)

	{

		double num = Math.Cos(angle);

		double num2 = Math.Sin(angle);

		double num3 = 1.0 - num;

		Vector3d normal = axis.GetNormal();

		double num4 = vector.DotProduct(normal);

		Vector3d val = normal.CrossProduct(vector);

		return vector * num + val * num2 + normal * num4 * num3;

	}



	private List<Point3d> RemoveDuplicateVertices(List<Point3d> vertices)

	{

		List<Point3d> list = new List<Point3d>();

		HashSet<long> hashSet = new HashSet<long>();

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			if (hashSet.Add(HashPoint3d(current)))

			{

				list.Add(current);

			}

		}

		return list;

	}



	private Point3d GetSolidCentroid(Solid3d solid)

	{

		try

		{

			Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

			Point3d val = geometricExtents.MinPoint;

			double x = val.X;

			val = geometricExtents.MaxPoint;

			double num = (x + val.X) / 2.0;

			val = geometricExtents.MinPoint;

			double y = val.Y;

			val = geometricExtents.MaxPoint;

			double num2 = (y + val.Y) / 2.0;

			val = geometricExtents.MinPoint;

			double z = val.Z;

			val = geometricExtents.MaxPoint;

			return new Point3d(num, num2, (z + val.Z) / 2.0);

		}

		catch

		{

			return Point3d.Origin;

		}

	}



	private double[] CalculateOBBFromRealVertices(List<Point3d> vertices)

	{

		try

		{

			if (vertices == null || vertices.Count < 4)

			{

				return CalculateAABBFromVertices(vertices ?? new List<Point3d>());

			}

			List<Point3d> list = RemoveDuplicateVertices(vertices);

			if (list.Count < 4)

			{

				return CalculateAABBFromVertices(list);

			}

			if (TryGetThicknessNormalFromVertices(list, out var normalAxis, out var _, out var _))

			{

				List<Point3d> list2 = RotatePointsToAlignNormalToZ(list, normalAxis);

				if (list2 != null)

				{

					double[] array = CalculateOBBForPlane(list2, "XY");

					if (array != null && ValidateOBBResult(array))

					{

						return array;

					}

				}

			}

			if (TryGetOrientedBoxFrame(list, out var axisX, out var axisY, out var axisZ, out var minX, out var maxX, out var minY, out var maxY, out var minZ, out var maxZ))

			{

				double[] array2 = new double[3]

				{

					Math.Abs(maxX - minX),

					Math.Abs(maxY - minY),

					Math.Abs(maxZ - minZ)

				};

				Array.Sort(array2, (double a, double b) => b.CompareTo(a));

				return array2;

			}

			double[] array3 = CalculateOBBByRotatingCalipers(list);

			if (array3 != null && ValidateOBBResult(array3))

			{

				return array3;

			}

			Point3d centroid = CalculateCentroid(list);

			List<Vector3d> list3 = CalculateSimplifiedPrincipalDirections(list, centroid);

			if (list3 != null && list3.Count >= 2)

			{

				Vector3d val = list3[0];

				axisX = val.GetNormal();

				val = list3[1];

				axisY = val.GetNormal();

				Vector3d val2 = axisX.CrossProduct(axisY);

				if (val2.Length > 1E-09)

				{

					axisZ = val2.GetNormal();

					val = axisZ.CrossProduct(axisX);

					axisY = val.GetNormal();

					if (TryGetProjectedExtents(list, axisX, axisY, axisZ, out minX, out maxX, out minY, out maxY, out minZ, out maxZ))

					{

						double[] array4 = new double[3]

						{

							Math.Abs(maxX - minX),

							Math.Abs(maxY - minY),

							Math.Abs(maxZ - minZ)

						};

						Array.Sort(array4, (double a, double b) => b.CompareTo(a));

						return array4;

					}

				}

			}

			return CalculateAABBFromVertices(list);

		}

		catch (Exception ex)

		{

			Debug.WriteLine("鐪熷疄椤剁偣OBB璁＄畻鍑洪敊: " + ex.Message);

			return CalculateAABBFromVertices(vertices ?? new List<Point3d>());

		}

	}



	private static List<Point3d> RotatePointsToAlignNormalToZ(List<Point3d> points, Vector3d normal)

	{

		if (points == null)

		{

			return null;

		}

		if (normal.Length <= 1E-09)

		{

			return new List<Point3d>(points);

		}

		Vector3d val = normal.GetNormal();

		if (val.DotProduct(Vector3d.ZAxis) < 0.0)

		{

			val = val.Negate();

		}

		double angleTo = val.GetAngleTo(Vector3d.ZAxis);

		if (angleTo <= 1E-09)

		{

			return new List<Point3d>(points);

		}

		Vector3d val2 = val.CrossProduct(Vector3d.ZAxis);

		if (val2.Length <= 1E-09)

		{

			return new List<Point3d>(points);

		}

		Matrix3d val3 = Matrix3d.Rotation(angleTo, val2.GetNormal(), Point3d.Origin);

		List<Point3d> list = new List<Point3d>(points.Count);

		foreach (Point3d point in points)

		{

			Point3d current = point;

			list.Add(current.TransformBy(val3));

		}

		return list;

	}



	private double[] CalculateOBBByRotatingCalipers(List<Point3d> vertices)

	{

		try

		{

			List<Point3d> list = RemoveDuplicateVertices(vertices);

			if (list.Count < 4)

			{

				return null;

			}

			Point3d val = CalculateCentroid(list);

			double[] array = TryMultipleProjections(list);

			if (array != null && array.Length == 3 && array.All((double d) => d > 0.0 && !double.IsNaN(d) && !double.IsInfinity(d)))

			{

				Array.Sort(array, (double a, double b) => b.CompareTo(a));

				return array;

			}

			return null;

		}

		catch (Exception ex)

		{

			Debug.WriteLine("鏃嬭浆鍗″昂绠楁硶鍑洪敊: " + ex.Message);

			return null;

		}

	}



	private double[] TryMultipleProjections(List<Point3d> vertices)

	{

		List<(double[], double)> list = new List<(double[], double)>();

		string[] array = new string[3] { "XY", "XZ", "YZ" };

		string[] array2 = array;

		foreach (string plane in array2)

		{

			double[] array3 = CalculateOBBForPlane(vertices, plane);

			if (array3 != null)

			{

				double item = array3[0] * array3[1] * array3[2];

				list.Add((array3, item));

			}

		}

		List<ProjectionPlane> list2 = GenerateObliquePlanes(vertices);

		foreach (ProjectionPlane item3 in list2)

		{

			double[] array4 = CalculateOBBForObliquePlane(vertices, item3.Normal, item3.UpVector);

			if (array4 != null)

			{

				double item2 = array4[0] * array4[1] * array4[2];

				list.Add((array4, item2));

			}

		}

		if (list.Count > 0)

		{

			(double[], double) tuple = list.OrderBy<(double[], double), double>(((double[] dimensions, double volume) r) => r.volume).First();

			if (ValidateOBBResult(tuple.Item1))

			{

				return tuple.Item1;

			}

		}

		return null;

	}



	private List<ProjectionPlane> GenerateObliquePlanes(List<Point3d> vertices)

	{

		List<ProjectionPlane> list = new List<ProjectionPlane>();

		Point3d centroid = CalculateCentroid(vertices);

		List<Vector3d> list2 = CalculateSimplifiedPrincipalDirections(vertices, centroid);

		if (list2 != null && list2.Count >= 2)

		{

			list.Add(new ProjectionPlane

			{

				Normal = list2[0],

				UpVector = list2[1]

			});

			list.Add(new ProjectionPlane

			{

				Normal = list2[1],

				UpVector = list2[0]

			});

			if (list2.Count >= 3)

			{

				list.Add(new ProjectionPlane

				{

					Normal = list2[2],

					UpVector = list2[0]

				});

			}

		}

		double[] array = new double[3]

		{

			Math.PI / 6.0,

			Math.PI / 4.0,

			Math.PI / 3.0

		};

		double[] array2 = array;

		Vector3d normal = default(Vector3d);

		Vector3d upVector = default(Vector3d);

		Vector3d normal2 = default(Vector3d);

		Vector3d upVector2 = default(Vector3d);

		Vector3d normal3 = default(Vector3d);

		Vector3d upVector3 = default(Vector3d);

		foreach (double num in array2)

		{

			normal = new Vector3d(0.0, Math.Cos(num), Math.Sin(num));

			upVector = new Vector3d(1.0, 0.0, 0.0);

			list.Add(new ProjectionPlane

			{

				Normal = normal,

				UpVector = upVector

			});

			normal2 = new Vector3d(Math.Sin(num), 0.0, Math.Cos(num));

			upVector2 = new Vector3d(0.0, 1.0, 0.0);

			list.Add(new ProjectionPlane

			{

				Normal = normal2,

				UpVector = upVector2

			});

			normal3 = new Vector3d(Math.Cos(num), Math.Sin(num), 0.0);

			upVector3 = new Vector3d(0.0, 0.0, 1.0);

			list.Add(new ProjectionPlane

			{

				Normal = normal3,

				UpVector = upVector3

			});

		}

		return list;

	}



	private double[] CalculateOBBForObliquePlane(List<Point3d> vertices, Vector3d normal, Vector3d upVector)

	{

		try

		{

			normal = normal.GetNormal();

			Vector3d val = upVector - normal * upVector.DotProduct(normal);

			upVector = val.GetNormal();

			val = upVector.CrossProduct(normal);

			Vector3d normal2 = val.GetNormal();

			List<Point2d> list = new List<Point2d>();

			double num = double.MaxValue;

			double num2 = double.MinValue;

			foreach (Point3d vertex in vertices)

			{

				Point3d current = vertex;

				double num3 = current.X * normal2.X + current.Y * normal2.Y + current.Z * normal2.Z;

				double num4 = current.X * upVector.X + current.Y * upVector.Y + current.Z * upVector.Z;

				double num5 = current.X * normal.X + current.Y * normal.Y + current.Z * normal.Z;

				list.Add(new Point2d(num3, num4));

				if (num5 < num)

				{

					num = num5;

				}

				if (num5 > num2)

				{

					num2 = num5;

				}

			}

			double val2 = Math.Abs(num2 - num);

			list = RemoveDuplicatePoints2D(list);

			if (list.Count < 3)

			{

				return null;

			}

			List<Point2d> list2 = ComputeConvexHull2D(list);

			if (list2.Count < 3)

			{

				return null;

			}

			BoundingRectangle boundingRectangle = FindMinimumBoundingRectangleImproved(list2);

			if (boundingRectangle == null)

			{

				return null;

			}

			val2 = Math.Max(val2, 0.1);

			double[] array = new double[3] { boundingRectangle.Width, boundingRectangle.Height, val2 };

			Array.Sort(array);

			Array.Reverse(array);

			return array;

		}

		catch

		{

			return null;

		}

	}



	private double[] CalculateOBBForPlane(List<Point3d> vertices, string plane)

	{

		try

		{

			List<Point2d> points;

			double val;

			switch (plane)

			{

			case "XY":

				points = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point2d>)((Point3d v) => new Point2d(v.X, v.Y))).ToList();

				val = Math.Abs(vertices.Max((Point3d v) => v.Z) - vertices.Min((Point3d v) => v.Z));

				break;

			case "XZ":

				points = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point2d>)((Point3d v) => new Point2d(v.X, v.Z))).ToList();

				val = Math.Abs(vertices.Max((Point3d v) => v.Y) - vertices.Min((Point3d v) => v.Y));

				break;

			case "YZ":

				points = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point2d>)((Point3d v) => new Point2d(v.Y, v.Z))).ToList();

				val = Math.Abs(vertices.Max((Point3d v) => v.X) - vertices.Min((Point3d v) => v.X));

				break;

			default:

				return null;

			}

			points = RemoveDuplicatePoints2D(points);

			if (points.Count < 3)

			{

				return null;

			}

			List<Point2d> list = ComputeConvexHull2D(points);

			if (list.Count < 3)

			{

				return null;

			}

			BoundingRectangle boundingRectangle = FindMinimumBoundingRectangle(list);

			if (boundingRectangle == null)

			{

				return null;

			}

			val = Math.Max(val, 0.1);

			double[] array = new double[3] { boundingRectangle.Width, boundingRectangle.Height, val };

			Array.Sort(array);

			Array.Reverse(array);

			return array;

		}

		catch

		{

			return null;

		}

	}



	private List<Point2d> RemoveDuplicatePoints2D(List<Point2d> points)

	{

		List<Point2d> list = new List<Point2d>();

		HashSet<long> hashSet = new HashSet<long>();

		foreach (Point2d point in points)

		{

			Point2d current = point;

			if (hashSet.Add(HashPoint2d(current)))

			{

				list.Add(current);

			}

		}

		return list;

	}



	private List<Point2d> ComputeConvexHull2D(List<Point2d> points)

	{

		if (points.Count < 3)

		{

			return points.ToList();

		}

		List<Point2d> list = RemoveDuplicatePoints2D(points);

		if (list.Count < 3)

		{

			return list;

		}

		List<Point2d> list2 = (from p in list

			orderby p.X, p.Y

			select p).ToList();

		List<Point2d> list3 = new List<Point2d>();

		for (int num = 0; num < list2.Count; num++)

		{

			while (list3.Count >= 2 && CrossProduct2D(list3[list3.Count - 2], list3[list3.Count - 1], list2[num]) <= 0.0)

			{

				list3.RemoveAt(list3.Count - 1);

			}

			list3.Add(list2[num]);

		}

		List<Point2d> list4 = new List<Point2d>();

		for (int num2 = list2.Count - 1; num2 >= 0; num2--)

		{

			while (list4.Count >= 2 && CrossProduct2D(list4[list4.Count - 2], list4[list4.Count - 1], list2[num2]) <= 0.0)

			{

				list4.RemoveAt(list4.Count - 1);

			}

			list4.Add(list2[num2]);

		}

		if (list3.Count > 0)

		{

			list3.RemoveAt(list3.Count - 1);

		}

		if (list4.Count > 0)

		{

			list4.RemoveAt(list4.Count - 1);

		}

		List<Point2d> list5 = new List<Point2d>();

		list5.AddRange(list3);

		list5.AddRange(list4);

		if (list5.Count < 3)

		{

			return list;

		}

		return list5;

	}



	private double CrossProduct2D(Point2d a, Point2d b, Point2d c)

	{

		return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

	}



	private BoundingRectangle FindMinimumBoundingRectangle(List<Point2d> convexHull)

	{

		return FindMinimumBoundingRectangleImproved(convexHull);

	}



	private BoundingRectangle FindMinimumBoundingRectangleImproved(List<Point2d> convexHull)

	{

		if (convexHull.Count < 3)

		{

			return null;

		}

		double num = double.MaxValue;

		BoundingRectangle boundingRectangle = null;

		if (!IsCounterClockwise(convexHull))

		{

			convexHull.Reverse();

		}

		BoundingRectangle boundingRectangle2 = FindMinimumBoundingRectangleByRotatingCalipers(convexHull);

		if (boundingRectangle2 != null && boundingRectangle2.Area < num)

		{

			num = boundingRectangle2.Area;

			boundingRectangle = boundingRectangle2;

		}

		if (boundingRectangle == null)

		{

			boundingRectangle = CalculateAxisAlignedBoundingRectangle(convexHull);

		}

		return boundingRectangle;

	}


	private BoundingRectangle FindMinimumBoundingRectangleByRotatingCalipers(List<Point2d> convexHull)

	{

		int count = convexHull.Count;

		if (count < 3)

		{

			return null;

		}

		Point2d val = convexHull[0];

		Point2d val2 = convexHull[1];

		double num = val2.X - val.X;

		double num2 = val2.Y - val.Y;

		double num3 = Math.Sqrt(num * num + num2 * num2);

		if (num3 <= 1E-10)

		{

			return null;

		}

		double num4 = num / num3;

		double num5 = num2 / num3;

		double num6 = 0.0 - num5;

		double num7 = num4;

		InitializeSupportIndices(convexHull, num4, num5, num6, num7, out var idxMaxU, out var idxMaxNegU, out var idxMaxV, out var idxMaxNegV);

		BoundingRectangle boundingRectangle = null;

		double num8 = double.MaxValue;

		for (int i = 0; i < count; i++)

		{

			Point2d val3 = convexHull[i];

			Point2d val4 = convexHull[(i + 1) % count];

			double num9 = val4.X - val3.X;

			double num10 = val4.Y - val3.Y;

			double num11 = Math.Sqrt(num9 * num9 + num10 * num10);

			if (num11 <= 1E-10)

			{

				continue;

			}

			double num12 = num9 / num11;

			double num13 = num10 / num11;

			double num14 = 0.0 - num13;

			double num15 = num12;

			idxMaxU = AdvanceMaxSupportIndex(convexHull, idxMaxU, num12, num13);

			idxMaxNegU = AdvanceMaxSupportIndex(convexHull, idxMaxNegU, 0.0 - num12, 0.0 - num13);

			idxMaxV = AdvanceMaxSupportIndex(convexHull, idxMaxV, num14, num15);

			idxMaxNegV = AdvanceMaxSupportIndex(convexHull, idxMaxNegV, 0.0 - num14, 0.0 - num15);

			double num16 = DotProduct2D(convexHull[idxMaxU], num12, num13);

			double num17 = DotProduct2D(convexHull[idxMaxNegU], 0.0 - num12, 0.0 - num13);

			double num18 = DotProduct2D(convexHull[idxMaxV], num14, num15);

			double num19 = DotProduct2D(convexHull[idxMaxNegV], 0.0 - num14, 0.0 - num15);

			double num20 = num16 + num17;

			double num21 = num18 + num19;

			if (num20 <= 1E-10 || num21 <= 1E-10)

			{

				continue;

			}

			double num22 = num20 * num21;

			if (num22 < num8)

			{

				num8 = num22;

				boundingRectangle = new BoundingRectangle

				{

					Width = num20,

					Height = num21,

					Area = num22,

					Angle = NormalizeRectangleAngle(Math.Atan2(num13, num12))

				};

			}

		}

		return boundingRectangle;

	}

	private void InitializeSupportIndices(List<Point2d> convexHull, double ux, double uy, double vx, double vy, out int idxMaxU, out int idxMaxNegU, out int idxMaxV, out int idxMaxNegV)

	{

		idxMaxU = 0;

		idxMaxNegU = 0;

		idxMaxV = 0;

		idxMaxNegV = 0;

		double num = DotProduct2D(convexHull[0], ux, uy);

		double num2 = DotProduct2D(convexHull[0], 0.0 - ux, 0.0 - uy);

		double num3 = DotProduct2D(convexHull[0], vx, vy);

		double num4 = DotProduct2D(convexHull[0], 0.0 - vx, 0.0 - vy);

		for (int i = 1; i < convexHull.Count; i++)

		{

			double num5 = DotProduct2D(convexHull[i], ux, uy);

			if (num5 > num)

			{

				num = num5;

				idxMaxU = i;

			}

			double num6 = DotProduct2D(convexHull[i], 0.0 - ux, 0.0 - uy);

			if (num6 > num2)

			{

				num2 = num6;

				idxMaxNegU = i;

			}

			double num7 = DotProduct2D(convexHull[i], vx, vy);

			if (num7 > num3)

			{

				num3 = num7;

				idxMaxV = i;

			}

			double num8 = DotProduct2D(convexHull[i], 0.0 - vx, 0.0 - vy);

			if (num8 > num4)

			{

				num4 = num8;

				idxMaxNegV = i;

			}

		}

	}

	private int AdvanceMaxSupportIndex(List<Point2d> convexHull, int startIndex, double axisX, double axisY)

	{

		int count = convexHull.Count;

		int num = startIndex;

		double num2 = DotProduct2D(convexHull[num], axisX, axisY);

		while (true)

		{

			int num3 = (num + 1) % count;

			double num4 = DotProduct2D(convexHull[num3], axisX, axisY);

			if (!(num4 > num2 + 1E-09))

			{

				break;

			}

			num = num3;

			num2 = num4;

		}

		return num;

	}

	private double DotProduct2D(Point2d point, double axisX, double axisY)

	{

		return point.X * axisX + point.Y * axisY;

	}

	private BoundingRectangle CalculateBoundingRectangleAtAngle(List<Point2d> convexHull, double angle)

	{

		try

		{

			List<Point2d> source = RotatePointsImproved(convexHull, 0.0 - angle);

			double num = source.Min((Point2d p) => p.X);

			double num2 = source.Max((Point2d p) => p.X);


			double num3 = source.Min((Point2d p) => p.Y);

			double num4 = source.Max((Point2d p) => p.Y);

			double num5 = num2 - num;

			double num6 = num4 - num3;

			if (num5 <= 1E-10 || num6 <= 1E-10)

			{

				return null;

			}

			return new BoundingRectangle

			{

				Width = num5,

				Height = num6,

				Area = num5 * num6,

				Angle = angle

			};

		}

		catch

		{

			return null;

		}

	}

	private List<double> BuildBoundingRectangleCandidateAngles(List<Point2d> convexHull)

	{

		List<double> list = new List<double>();

		HashSet<long> hashSet = new HashSet<long>();

		for (int i = 0; i < convexHull.Count; i++)

		{

			Point2d val = convexHull[i];

			Point2d val2 = convexHull[(i + 1) % convexHull.Count];

			double num = val2.X - val.X;

			double num2 = val2.Y - val.Y;

			if (Math.Abs(num) <= 1E-10 && Math.Abs(num2) <= 1E-10)

			{

				continue;

			}

			AddBoundingRectangleAngleCandidate(list, hashSet, Math.Atan2(num2, num));

			AddBoundingRectangleAngleCandidate(list, hashSet, Math.Atan2(num2, num) + Math.PI / 2.0);

		}

		if (list.Count == 0)

		{

			list.Add(0.0);

		}

		list.Sort();

		return list;

	}

	private void AddBoundingRectangleAngleCandidate(List<double> angles, HashSet<long> angleKeys, double angle)

	{

		double num = NormalizeRectangleAngle(angle);

		long item = (long)Math.Round(num * 1000000.0);

		if (angleKeys.Add(item))

		{

			angles.Add(num);

		}

	}

	private double NormalizeRectangleAngle(double angle)

	{

		double num = angle % (Math.PI / 2.0);

		if (num < 0.0)

		{

			num += Math.PI / 2.0;

		}

		return num;

	}

	private long HashPoint3d(Point3d point)

	{

		long num = (long)Math.Round(point.X * 1000000.0);

		long num2 = (long)Math.Round(point.Y * 1000000.0);

		long num3 = (long)Math.Round(point.Z * 1000000.0);

		return (num * 73856093L) ^ (num2 * 19349663L) ^ (num3 * 83492791L);

	}

	private long HashPoint2d(Point2d point)

	{

		long num = (long)Math.Round(point.X * 1000000.0);

		long num2 = (long)Math.Round(point.Y * 1000000.0);

		return (num * 73856093L) ^ (num2 * 19349663L);

	}



	private BoundingRectangle CalculateAxisAlignedBoundingRectangle(List<Point2d> points)

	{

		if (points.Count == 0)

		{

			return null;

		}

		double num = points.Min((Point2d p) => p.X);

		double num2 = points.Max((Point2d p) => p.X);

		double num3 = points.Min((Point2d p) => p.Y);

		double num4 = points.Max((Point2d p) => p.Y);

		double val = num2 - num;

		double val2 = num4 - num3;

		val = Math.Max(val, 0.1);

		val2 = Math.Max(val2, 0.1);

		return new BoundingRectangle

		{

			Width = val,

			Height = val2,

			Area = val * val2,

			Angle = 0.0

		};

	}



	private bool IsCounterClockwise(List<Point2d> points)

	{

		if (points.Count < 3)

		{

			return true;

		}

		double num = 0.0;

		for (int i = 0; i < points.Count; i++)

		{

			Point2d val = points[i];

			Point2d val2 = points[(i + 1) % points.Count];

			num += (val2.X - val.X) * (val2.Y + val.Y);

		}

		return num < 0.0;

	}



	private List<Point2d> RotatePointsImproved(List<Point2d> points, double angle)

	{

		double num = Math.Cos(angle);

		double num2 = Math.Sin(angle);

		List<Point2d> list = new List<Point2d>();

		foreach (Point2d point in points)

		{

			Point2d current = point;

			double num3 = current.X * num - current.Y * num2;

			double num4 = current.X * num2 + current.Y * num;

			list.Add(new Point2d(num3, num4));

		}

		return list;

	}



	private List<Point2d> RotatePoints(List<Point2d> points, double angle)

	{

		double cos = Math.Cos(angle);

		double sin = Math.Sin(angle);

		return ((IEnumerable<Point2d>)points).Select((Func<Point2d, Point2d>)((Point2d p) => new Point2d(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos))).ToList();

	}



	private double[] CalculateAABBFromVertices(List<Point3d> vertices)

	{

		if (vertices == null || vertices.Count == 0)

		{

			return new double[3];

		}

		double num = vertices.Min((Point3d v) => v.X);

		double num2 = vertices.Max((Point3d v) => v.X);

		double num3 = vertices.Min((Point3d v) => v.Y);

		double num4 = vertices.Max((Point3d v) => v.Y);

		double num5 = vertices.Min((Point3d v) => v.Z);

		double num6 = vertices.Max((Point3d v) => v.Z);

		double[] array = new double[3]

		{

			Math.Abs(num2 - num),

			Math.Abs(num4 - num3),

			Math.Abs(num6 - num5)

		};

		for (int num7 = 0; num7 < array.Length; num7++)

		{

			if (array[num7] < 0.1)

			{

				array[num7] = 0.1;

			}

		}

		Array.Sort(array);

		Array.Reverse(array);

		return array;

	}



	private bool ValidateOBBResult(double[] dimensions)

	{

		return ValidateOBBResultImproved(dimensions, null);

	}



	private bool ValidateOBBResultImproved(double[] dimensions, List<Point3d> originalVertices)

	{

		if (!IsValidDimensionsArray(dimensions))

		{

			return false;

		}

		if (!AreDimensionsReasonable(dimensions[0], dimensions[1], dimensions[2]))

		{

			return false;

		}

		if (originalVertices != null && originalVertices.Count > 0)

		{

			return ValidateAgainstOriginalGeometry(dimensions[0], dimensions[1], dimensions[2], originalVertices);

		}

		return true;

	}



	private bool IsValidDimensionsArray(double[] dimensions)

	{

		if (dimensions == null || dimensions.Length != 3)

		{

			return false;

		}

		foreach (double dimension in dimensions)

		{

			if (!IsValidDimension(dimension))

			{

				return false;

			}

		}

		return true;

	}



	private bool IsValidDimension(double dimension)

	{

		if (double.IsNaN(dimension) || double.IsInfinity(dimension))

		{

			return false;

		}

		if (dimension <= 0.0)

		{

			return false;

		}

		return dimension >= 1E-08 && dimension <= 100000000.0;

	}



	private bool AreDimensionsReasonable(double width, double height, double depth)

	{

		double[] source = new double[3] { width, height, depth };

		double num = source.Max();

		double num2 = source.Min();

		if (num / num2 > 1000000.0)

		{

			return false;

		}

		double num3 = width * height * depth;

		if (num3 < 1E-15 || num3 > 1000000000000000.0)

		{

			return false;

		}

		return true;

	}



	private bool ValidateAgainstOriginalGeometry(double width, double height, double depth, List<Point3d> originalVertices)

	{

		try

		{

			double[] array = CalculateAABBDimensions(originalVertices);

			if (array == null)

			{

				return true;

			}

			double num = width * height * depth;

			double num2 = array[0] * array[1] * array[2];

			if (num > num2 * 1.1)

			{

				return false;

			}

			double[] array2 = new double[3] { width, height, depth }.OrderByDescending((double x) => x).ToArray();

			double[] array3 = array.OrderByDescending((double x) => x).ToArray();

			for (int num3 = 0; num3 < 3; num3++)

			{

				if (array2[num3] > array3[num3] * 1.05)

				{

					return false;

				}

			}

			return true;

		}

		catch

		{

			return true;

		}

	}



	private double[] CalculateAABBDimensions(List<Point3d> vertices)

	{

		if (vertices == null || vertices.Count == 0)

		{

			return null;

		}

		double num = vertices.Min((Point3d v) => v.X);

		double num2 = vertices.Max((Point3d v) => v.X);

		double num3 = vertices.Min((Point3d v) => v.Y);

		double num4 = vertices.Max((Point3d v) => v.Y);

		double num5 = vertices.Min((Point3d v) => v.Z);

		double num6 = vertices.Max((Point3d v) => v.Z);

		return new double[3]

		{

			Math.Abs(num2 - num),

			Math.Abs(num4 - num3),

			Math.Abs(num6 - num5)

		};

	}



	private List<Point3d> RotateVertices(List<Point3d> vertices, Point3d centroid, double radX, double radY, double radZ)

	{

		List<Point3d> list = new List<Point3d>();

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double num = current.X - centroid.X;

			double num2 = current.Y - centroid.Y;

			double num3 = current.Z - centroid.Z;

			double num4 = Math.Cos(radX);

			double num5 = Math.Sin(radX);

			double num6 = num2 * num4 - num3 * num5;

			double num7 = num2 * num5 + num3 * num4;

			double num8 = Math.Cos(radY);

			double num9 = Math.Sin(radY);

			double num10 = num * num8 + num7 * num9;

			double num11 = (0.0 - num) * num9 + num7 * num8;

			double num12 = Math.Cos(radZ);

			double num13 = Math.Sin(radZ);

			double num14 = num10 * num12 - num6 * num13;

			double num15 = num10 * num13 + num6 * num12;

			list.Add(new Point3d(num14 + centroid.X, num15 + centroid.Y, num11 + centroid.Z));

		}

		return list;

	}



	private List<Point3d> ComputeConvexHull(List<Point3d> vertices)

	{

		try

		{

			if (vertices.Count < 4)

			{

				return vertices;

			}

			List<Point3d> list = new List<Point3d>();

			Point3d item = vertices.OrderBy((Point3d p) => p.X).First();

			Point3d item2 = vertices.OrderByDescending((Point3d p) => p.X).First();

			Point3d item3 = vertices.OrderBy((Point3d p) => p.Y).First();

			Point3d item4 = vertices.OrderByDescending((Point3d p) => p.Y).First();

			Point3d item5 = vertices.OrderBy((Point3d p) => p.Z).First();

			Point3d item6 = vertices.OrderByDescending((Point3d p) => p.Z).First();

			list.Add(item);

			list.Add(item2);

			list.Add(item3);

			list.Add(item4);

			list.Add(item5);

			list.Add(item6);

			Point3d centroid = CalculateCentroid(vertices);

			IEnumerable<Point3d> collection = from x in (from v in vertices

					select new

					{

						Point = v,

						Distance = DistanceToPoint(v, centroid)

					} into x

					orderby x.Distance descending

					select x).Take(Math.Min(20, vertices.Count / 4))

				select x.Point;

			list.AddRange(collection);

			List<Point3d> list2 = (from p in list

				group p by new

				{

					X = Math.Round(p.X, 6),

					Y = Math.Round(p.Y, 6),

					Z = Math.Round(p.Z, 6)

				} into g

				select g.First()).ToList();

			return (list2.Count >= 4) ? list2 : vertices;

		}

		catch

		{

			return vertices;

		}

	}



	private double DistanceToPoint(Point3d p1, Point3d p2)

	{

		double num = p1.X - p2.X;

		double num2 = p1.Y - p2.Y;

		double num3 = p1.Z - p2.Z;

		return Math.Sqrt(num * num + num2 * num2 + num3 * num3);

	}



	private List<Point3d> ProjectVertices(List<Point3d> vertices, Point3d centroid, Vector3d[] eigenVectors)

	{

		List<Point3d> list = new List<Point3d>();

		Vector3d val = default(Vector3d);

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			val = new Vector3d(current.X - centroid.X, current.Y - centroid.Y, current.Z - centroid.Z);

			double num = val.DotProduct(eigenVectors[0]);

			double num2 = val.DotProduct(eigenVectors[1]);

			double num3 = val.DotProduct(eigenVectors[2]);

			list.Add(new Point3d(num, num2, num3));

		}

		return list;

	}



	private List<Point3d> ProjectVertices(List<Point3d> vertices, List<Vector3d> directions, Point3d centroid)

	{

		List<Point3d> list = new List<Point3d>();

		Vector3d val = default(Vector3d);

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			val = new Vector3d(current.X - centroid.X, current.Y - centroid.Y, current.Z - centroid.Z);

			double num = val.DotProduct(directions[0]);

			double num2 = val.DotProduct(directions[1]);

			double num3 = val.DotProduct(directions[2]);

			list.Add(new Point3d(num, num2, num3));

		}

		return list;

	}



	private double[] CalculateProjectedDimensions(List<Point3d> projectedVertices)

	{

		if (projectedVertices.Count == 0)

		{

			return null;

		}

		double num = projectedVertices.Min((Point3d p) => p.X);

		double num2 = projectedVertices.Max((Point3d p) => p.X);

		double num3 = projectedVertices.Min((Point3d p) => p.Y);

		double num4 = projectedVertices.Max((Point3d p) => p.Y);

		double num5 = projectedVertices.Min((Point3d p) => p.Z);

		double num6 = projectedVertices.Max((Point3d p) => p.Z);

		double num7 = Math.Round(Math.Abs(num2 - num), 2);

		double num8 = Math.Round(Math.Abs(num4 - num3), 2);

		double num9 = Math.Round(Math.Abs(num6 - num5), 2);

		return new double[3] { num7, num8, num9 }.OrderByDescending((double d) => d).ToArray();

	}



	private List<Point3d> RotateVerticesAroundOrigin(List<Point3d> vertices, Point3d centroid, double radX, double radY, double radZ)

	{

		List<Point3d> list = new List<Point3d>();

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double num = current.X - centroid.X;

			double num2 = current.Y - centroid.Y;

			double num3 = current.Z - centroid.Z;

			double num4 = Math.Cos(radX);

			double num5 = Math.Sin(radX);

			double num6 = num2 * num4 - num3 * num5;

			double num7 = num2 * num5 + num3 * num4;

			double num8 = Math.Cos(radY);

			double num9 = Math.Sin(radY);

			double num10 = num * num8 + num7 * num9;

			double num11 = (0.0 - num) * num9 + num7 * num8;

			double num12 = Math.Cos(radZ);

			double num13 = Math.Sin(radZ);

			double num14 = num10 * num12 - num6 * num13;

			double num15 = num10 * num13 + num6 * num12;

			list.Add(new Point3d(num14, num15, num11));

		}

		return list;

	}



	private FaceInfo FindLargestFace(List<Point3d> vertices)

	{

		try

		{

			List<FaceInfo> list = new List<FaceInfo>();

			List<IGrouping<double, Point3d>> list2 = (from v in vertices

				group v by Math.Round(v.X, 3) into g

				where g.Count() >= 4

				select g).ToList();

			foreach (IGrouping<double, Point3d> item in list2)

			{

				List<Point3d> list3 = item.ToList();

				if (list3.Count >= 4)

				{

					double area = CalculateFaceArea(list3);

					list.Add(new FaceInfo

					{

						Normal = Vector3d.XAxis,

						Area = area,

						Vertices = list3

					});

				}

			}

			List<IGrouping<double, Point3d>> list4 = (from v in vertices

				group v by Math.Round(v.Y, 3) into g

				where g.Count() >= 4

				select g).ToList();

			foreach (IGrouping<double, Point3d> item2 in list4)

			{

				List<Point3d> list5 = item2.ToList();

				if (list5.Count >= 4)

				{

					double area2 = CalculateFaceArea(list5);

					list.Add(new FaceInfo

					{

						Normal = Vector3d.YAxis,

						Area = area2,

						Vertices = list5

					});

				}

			}

			List<IGrouping<double, Point3d>> list6 = (from v in vertices

				group v by Math.Round(v.Z, 3) into g

				where g.Count() >= 4

				select g).ToList();

			foreach (IGrouping<double, Point3d> item3 in list6)

			{

				List<Point3d> list7 = item3.ToList();

				if (list7.Count >= 4)

				{

					double area3 = CalculateFaceArea(list7);

					list.Add(new FaceInfo

					{

						Normal = Vector3d.ZAxis,

						Area = area3,

						Vertices = list7

					});

				}

			}

			return list.OrderByDescending((FaceInfo f) => f.Area).FirstOrDefault();

		}

		catch

		{

			return null;

		}

	}



	private double CalculateFaceArea(List<Point3d> faceVertices)

	{

		try

		{

			if (faceVertices.Count < 3)

			{

				return 0.0;

			}

			double num = faceVertices.Min((Point3d p) => p.X);

			double num2 = faceVertices.Max((Point3d p) => p.X);

			double num3 = faceVertices.Min((Point3d p) => p.Y);

			double num4 = faceVertices.Max((Point3d p) => p.Y);

			double num5 = faceVertices.Min((Point3d p) => p.Z);

			double num6 = faceVertices.Max((Point3d p) => p.Z);

			double num7 = Math.Max(Math.Max(num2 - num, num4 - num3), num6 - num5);

			double num8 = Math.Max(Math.Max(num2 - num, num4 - num3), num6 - num5);

			if (Math.Abs(num2 - num) < 0.001)

			{

				return (num4 - num3) * (num6 - num5);

			}

			if (Math.Abs(num4 - num3) < 0.001)

			{

				return (num2 - num) * (num6 - num5);

			}

			if (Math.Abs(num6 - num5) < 0.001)

			{

				return (num2 - num) * (num4 - num3);

			}

			return num7 * num8;

		}

		catch

		{

			return 0.0;

		}

	}



	private double[] CalculateOBBUsingSimplifiedPCA(List<Point3d> vertices)

	{

		try

		{

			if (vertices == null || vertices.Count < 4)

			{

				return null;

			}

			List<Point3d> list = RemoveDuplicateVertices(vertices);

			if (list.Count < 4)

			{

				return null;

			}

			double[] array = CalculateOBBForThinPlate(list);

			if (array != null && ValidateOBBDimensions(array))

			{

				return array;

			}

			double[] array2 = CalculateOBBFromGeometry(list);

			if (array2 != null && ValidateOBBDimensions(array2))

			{

				return array2;

			}

			double[] array3 = CalculateOBBFromConvexHull(list);

			if (array3 != null && ValidateOBBDimensions(array3))

			{

				return array3;

			}

			return CalculateAABBDimensions(list);

		}

		catch (Exception ex)

		{

			Debug.WriteLine("OBB璁＄畻鍑洪敊: " + ex.Message);

			return CalculateAABBDimensions(vertices);

		}

	}



	private double[] CalculateOBBForThinPlate(List<Point3d> vertices)

	{

		try

		{

			Point3d centroid = CalculateCentroid(vertices);

			List<Point3d> list = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point3d>)((Point3d v) => new Point3d(v.X - centroid.X, v.Y - centroid.Y, v.Z - centroid.Z))).ToList();

			double[] array = CalculateAABBDimensions(list);

			if (array == null)

			{

				return null;

			}

			Array.Sort(array);

			double num = array[0] / array[2];

			if (num < 0.1)

			{

				return CalculateThinPlateOBBHighPrecision(list);

			}

			return null;

		}

		catch

		{

			return null;

		}

	}



	private double[] CalculateThinPlateOBBHighPrecision(List<Point3d> centeredVertices)

	{

		try

		{

			double bestVolume = double.MaxValue;

			double[] array = null;

			int angleStep = 1;

			array = SearchSingleAxisRotations(centeredVertices, angleStep, ref bestVolume);

			if (array != null)

			{

				double[] array2 = array.OrderBy((double d) => d).ToArray();

				double num = array2[0] / array2[2];

				if (num > 0.05)

				{

					double[] array3 = SearchDualAxisRotations(centeredVertices, ref bestVolume);

					if (array3 != null)

					{

						array = array3;

					}

				}

			}

			return array;

		}

		catch

		{

			return null;

		}

	}



	private double[] SearchSingleAxisRotations(List<Point3d> centeredVertices, int angleStep, ref double bestVolume)

	{

		double[] result = null;

		for (int i = 0; i < 180; i += angleStep)

		{

			double radZ = (double)i * Math.PI / 180.0;

			List<Point3d> vertices = RotateVerticesAroundZAxis(centeredVertices, radZ);

			double[] array = CalculateAABBDimensions(vertices);

			if (array == null || !array.All((double d) => d > 0.0))

			{

				continue;

			}

			double num = array[0] * array[1] * array[2];

			if (num < bestVolume)

			{

				bestVolume = num;

				result = array.OrderByDescending((double d) => d).ToArray();

			}

		}

		for (int num2 = 0; num2 < 180; num2 += angleStep)

		{

			double radY = (double)num2 * Math.PI / 180.0;

			List<Point3d> vertices2 = RotateVerticesAroundYAxis(centeredVertices, radY);

			double[] array2 = CalculateAABBDimensions(vertices2);

			if (array2 == null || !array2.All((double d) => d > 0.0))

			{

				continue;

			}

			double num3 = array2[0] * array2[1] * array2[2];

			if (num3 < bestVolume)

			{

				bestVolume = num3;

				result = array2.OrderByDescending((double d) => d).ToArray();

			}

		}

		for (int num4 = 0; num4 < 180; num4 += angleStep)

		{

			double radX = (double)num4 * Math.PI / 180.0;

			List<Point3d> vertices3 = RotateVerticesAroundXAxis(centeredVertices, radX);

			double[] array3 = CalculateAABBDimensions(vertices3);

			if (array3 == null || !array3.All((double d) => d > 0.0))

			{

				continue;

			}

			double num5 = array3[0] * array3[1] * array3[2];

			if (num5 < bestVolume)

			{

				bestVolume = num5;

				result = array3.OrderByDescending((double d) => d).ToArray();

			}

		}

		return result;

	}



	private double[] SearchDualAxisRotations(List<Point3d> centeredVertices, ref double bestVolume)

	{

		double[] result = null;

		int num = 5;

		for (int i = 0; i < 90; i += num)

		{

			for (int j = 0; j < 90; j += num)

			{

				double radX = (double)i * Math.PI / 180.0;

				double radY = (double)j * Math.PI / 180.0;

				List<Point3d> vertices = RotateVerticesAroundXAxis(centeredVertices, radX);

				vertices = RotateVerticesAroundYAxis(vertices, radY);

				double[] array = CalculateAABBDimensions(vertices);

				if (array == null || !array.All((double d) => d > 0.0))

				{

					continue;

				}

				double num2 = array[0] * array[1] * array[2];

				if (num2 < bestVolume)

				{

					bestVolume = num2;

					result = array.OrderByDescending((double d) => d).ToArray();

				}

			}

		}

		for (int num3 = 0; num3 < 90; num3 += num)

		{

			for (int num4 = 0; num4 < 90; num4 += num)

			{

				double radX2 = (double)num3 * Math.PI / 180.0;

				double radZ = (double)num4 * Math.PI / 180.0;

				List<Point3d> vertices2 = RotateVerticesAroundXAxis(centeredVertices, radX2);

				vertices2 = RotateVerticesAroundZAxis(vertices2, radZ);

				double[] array2 = CalculateAABBDimensions(vertices2);

				if (array2 == null || !array2.All((double d) => d > 0.0))

				{

					continue;

				}

				double num5 = array2[0] * array2[1] * array2[2];

				if (num5 < bestVolume)

				{

					bestVolume = num5;

					result = array2.OrderByDescending((double d) => d).ToArray();

				}

			}

		}

		for (int num6 = 0; num6 < 90; num6 += num)

		{

			for (int num7 = 0; num7 < 90; num7 += num)

			{

				double radY2 = (double)num6 * Math.PI / 180.0;

				double radZ2 = (double)num7 * Math.PI / 180.0;

				List<Point3d> vertices3 = RotateVerticesAroundYAxis(centeredVertices, radY2);

				vertices3 = RotateVerticesAroundZAxis(vertices3, radZ2);

				double[] array3 = CalculateAABBDimensions(vertices3);

				if (array3 == null || !array3.All((double d) => d > 0.0))

				{

					continue;

				}

				double num8 = array3[0] * array3[1] * array3[2];

				if (num8 < bestVolume)

				{

					bestVolume = num8;

					result = array3.OrderByDescending((double d) => d).ToArray();

				}

			}

		}

		return result;

	}



	private List<Point3d> RotateVerticesAroundZAxis(List<Point3d> vertices, double radZ)

	{

		List<Point3d> list = new List<Point3d>();

		double num = Math.Cos(radZ);

		double num2 = Math.Sin(radZ);

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double num3 = current.X * num - current.Y * num2;

			double num4 = current.X * num2 + current.Y * num;

			double z = current.Z;

			list.Add(new Point3d(num3, num4, z));

		}

		return list;

	}



	private List<Point3d> RotateVerticesAroundYAxis(List<Point3d> vertices, double radY)

	{

		List<Point3d> list = new List<Point3d>();

		double num = Math.Cos(radY);

		double num2 = Math.Sin(radY);

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double num3 = current.X * num + current.Z * num2;

			double y = current.Y;

			double num4 = (0.0 - current.X) * num2 + current.Z * num;

			list.Add(new Point3d(num3, y, num4));

		}

		return list;

	}



	private List<Point3d> RotateVerticesAroundXAxis(List<Point3d> vertices, double radX)

	{

		List<Point3d> list = new List<Point3d>();

		double num = Math.Cos(radX);

		double num2 = Math.Sin(radX);

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double x = current.X;

			double num3 = current.Y * num - current.Z * num2;

			double num4 = current.Y * num2 + current.Z * num;

			list.Add(new Point3d(x, num3, num4));

		}

		return list;

	}



	private double[] CalculateOBBFromGeometry(List<Point3d> vertices)

	{

		try

		{

			List<double> list = GroupSimilarValues(vertices.Select((Point3d v) => v.X), 1E-06);

			List<double> list2 = GroupSimilarValues(vertices.Select((Point3d v) => v.Y), 1E-06);

			List<double> list3 = GroupSimilarValues(vertices.Select((Point3d v) => v.Z), 1E-06);

			if (list.Count == 2 && list2.Count == 2 && list3.Count == 2)

			{

				double num = Math.Abs(list[1] - list[0]);

				double num2 = Math.Abs(list2[1] - list2[0]);

				double num3 = Math.Abs(list3[1] - list3[0]);

				if (num > 1E-06 && num2 > 1E-06 && num3 > 1E-06)

				{

					return new double[3] { num, num2, num3 }.OrderByDescending((double d) => d).ToArray();

				}

			}

			return DetectRotatedBox(vertices);

		}

		catch

		{

			return null;

		}

	}



	private double[] CalculateOBBFromConvexHull(List<Point3d> vertices)

	{

		try

		{

			Point3d centroid = CalculateCentroid(vertices);

			List<Point3d> centeredVertices = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point3d>)((Point3d v) => new Point3d(v.X - centroid.X, v.Y - centroid.Y, v.Z - centroid.Z))).ToList();

			double[] array = FindOptimalOrientationBruteForce(centeredVertices);

			if (array != null)

			{

				return array;

			}

			return CalculateOBBUsingRobustPCA(vertices);

		}

		catch

		{

			return null;

		}

	}



	private List<double> GroupSimilarValues(IEnumerable<double> values, double tolerance)

	{

		List<double> list = values.OrderBy((double v) => v).ToList();

		List<double> list2 = new List<double>();

		if (list.Count == 0)

		{

			return list2;

		}

		list2.Add(list[0]);

		for (int num = 1; num < list.Count; num++)

		{

			if (Math.Abs(list[num] - list2.Last()) > tolerance)

			{

				list2.Add(list[num]);

			}

		}

		return list2;

	}



	private double[] DetectRotatedBox(List<Point3d> vertices)

	{

		try

		{

			Point3d centroid = CalculateCentroid(vertices);

			List<Point3d> vertices2 = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point3d>)((Point3d v) => new Point3d(v.X - centroid.X, v.Y - centroid.Y, v.Z - centroid.Z))).ToList();

			double num = double.MaxValue;

			double[] result = null;

			for (int num2 = 0; num2 < 360; num2 += 15)

			{

				for (int num3 = 0; num3 < 360; num3 += 15)

				{

					for (int num4 = 0; num4 < 360; num4 += 15)

					{

						double radX = (double)num2 * Math.PI / 180.0;

						double radY = (double)num3 * Math.PI / 180.0;

						double radZ = (double)num4 * Math.PI / 180.0;

						List<Point3d> vertices3 = RotateVerticesAroundOrigin(vertices2, Point3d.Origin, radX, radY, radZ);

						double[] array = CalculateAABBDimensions(vertices3);

						if (array != null)

						{

							double num5 = array[0] * array[1] * array[2];

							if (num5 < num)

							{

								num = num5;

								result = array;

							}

						}

					}

				}

			}

			return result;

		}

		catch

		{

			return null;

		}

	}



	private double[] FindOptimalOrientationBruteForce(List<Point3d> centeredVertices)

	{

		try

		{

			double num = double.MaxValue;

			double[] result = null;

			int num2 = 10;

			for (int i = 0; i < 180; i += num2)

			{

				for (int j = 0; j < 180; j += num2)

				{

					for (int k = 0; k < 180; k += num2)

					{

						double radX = (double)i * Math.PI / 180.0;

						double radY = (double)j * Math.PI / 180.0;

						double radZ = (double)k * Math.PI / 180.0;

						List<Point3d> vertices = RotateVerticesAroundOrigin(centeredVertices, Point3d.Origin, radX, radY, radZ);

						double[] array = CalculateAABBDimensions(vertices);

						if (array != null && array.All((double d) => d > 0.0))

						{

							double num3 = array[0] * array[1] * array[2];

							if (num3 < num)

							{

								num = num3;

								result = array;

							}

						}

					}

				}

			}

			return result;

		}

		catch

		{

			return null;

		}

	}



	private double[] CalculateOBBUsingRobustPCA(List<Point3d> vertices)

	{

		try

		{

			Point3d centroid = CalculateCentroid(vertices);

			List<Point3d> list = ((IEnumerable<Point3d>)vertices).Select((Func<Point3d, Point3d>)((Point3d v) => new Point3d(v.X - centroid.X, v.Y - centroid.Y, v.Z - centroid.Z))).ToList();

			double[,] array = new double[3, 3];

			int count = list.Count;

			foreach (Point3d item in list)

			{

				Point3d current = item;

				array[0, 0] += current.X * current.X;

				array[0, 1] += current.X * current.Y;

				array[0, 2] += current.X * current.Z;

				array[1, 1] += current.Y * current.Y;

				array[1, 2] += current.Y * current.Z;

				array[2, 2] += current.Z * current.Z;

			}

			array[1, 0] = array[0, 1];

			array[2, 0] = array[0, 2];

			array[2, 1] = array[1, 2];

			for (int num = 0; num < 3; num++)

			{

				for (int num2 = 0; num2 < 3; num2++)

				{

					array[num, num2] /= count;

				}

			}

			EigenDecompositionResult eigenDecompositionResult = JacobiEigenDecomposition(array);

			if (eigenDecompositionResult != null)

			{

				Vector3d[] array2 = (Vector3d[])(object)new Vector3d[3];

				for (int num3 = 0; num3 < 3; num3++)

				{

					int num4 = num3;

					Vector3d val = new Vector3d(eigenDecompositionResult.EigenVectors[0, num3], eigenDecompositionResult.EigenVectors[1, num3], eigenDecompositionResult.EigenVectors[2, num3]);

					array2[num4] = val.GetNormal();

				}

				return CalculateDimensionsFromEigenVectors(list, array2);

			}

			return null;

		}

		catch

		{

			return null;

		}

	}



	private double[] CalculateDimensionsFromEigenVectors(List<Point3d> vertices, Vector3d[] eigenVectors)

	{

		double[] array = new double[3];

		int i;

		for (i = 0; i < 3; i++)

		{

			Vector3d eigenVector = eigenVectors[i];

			double[] source = vertices.Select((Point3d v) => v.X * eigenVector.X + v.Y * eigenVector.Y + v.Z * eigenVector.Z).ToArray();

			array[i] = source.Max() - source.Min();

		}

		Array.Sort(array);

		Array.Reverse(array);

		return array;

	}



	private EigenDecompositionResult JacobiEigenDecomposition(double[,] matrix)

	{

		try

		{

			double[,] array = new double[3, 3];

			double[,] array2 = new double[3, 3];

			for (int i = 0; i < 3; i++)

			{

				for (int j = 0; j < 3; j++)

				{

					array[i, j] = matrix[i, j];

					array2[i, j] = ((i == j) ? 1.0 : 0.0);

				}

			}

			for (int k = 0; k < 50; k++)

			{

				double num = 0.0;

				int num2 = 0;

				int num3 = 1;

				for (int l = 0; l < 3; l++)

				{

					for (int m = l + 1; m < 3; m++)

					{

						if (Math.Abs(array[l, m]) > num)

						{

							num = Math.Abs(array[l, m]);

							num2 = l;

							num3 = m;

						}

					}

				}

				if (num < 1E-10)

				{

					break;

				}

				double num4 = 0.5 * Math.Atan2(2.0 * array[num2, num3], array[num3, num3] - array[num2, num2]);

				double c = Math.Cos(num4);

				double s = Math.Sin(num4);

				ApplyGivensRotation(array, array2, num2, num3, c, s);

			}

			EigenDecompositionResult eigenDecompositionResult = new EigenDecompositionResult();

			eigenDecompositionResult.EigenValues = new double[3]

			{

				array[0, 0],

				array[1, 1],

				array[2, 2]

			};

			eigenDecompositionResult.EigenVectors = array2;

			return eigenDecompositionResult;

		}

		catch

		{

			return null;

		}

	}



	private void ApplyGivensRotation(double[,] A, double[,] V, int p, int q, double c, double s)

	{

		for (int i = 0; i < 3; i++)

		{

			if (i != p && i != q)

			{

				double num = A[i, p];

				double num2 = A[i, q];

				A[i, p] = c * num - s * num2;

				A[p, i] = A[i, p];

				A[i, q] = s * num + c * num2;

				A[q, i] = A[i, q];

			}

		}

		double num3 = A[p, p];

		double num4 = A[q, q];

		double num5 = A[p, q];

		A[p, p] = c * c * num3 + s * s * num4 - 2.0 * c * s * num5;

		A[q, q] = s * s * num3 + c * c * num4 + 2.0 * c * s * num5;

		A[p, q] = 0.0;

		A[q, p] = 0.0;

		for (int j = 0; j < 3; j++)

		{

			double num6 = V[j, p];

			double num7 = V[j, q];

			V[j, p] = c * num6 - s * num7;

			V[j, q] = s * num6 + c * num7;

		}

	}



	private bool ValidateOBBDimensions(double[] dimensions)

	{

		if (dimensions == null || dimensions.Length != 3)

		{

			return false;

		}

		if (dimensions.Any((double d) => d <= 0.0 || double.IsNaN(d) || double.IsInfinity(d)))

		{

			return false;

		}

		double num = dimensions.Max();

		double num2 = dimensions.Min();

		if (num / num2 > 1000.0)

		{

			return false;

		}

		return true;

	}



	private double[] CalculateOBBUsingPCA(List<Point3d> vertices)

	{

		try

		{

			if (vertices.Count < 4)

			{

				return null;

			}

			Point3d centroid = CalculateCentroid(vertices);

			double[,] matrix = CalculateCovarianceMatrix(vertices, centroid);

			Vector3d[] array = CalculateEigenVectors(matrix);

			if (array == null || array.Length != 3)

			{

				return CalculateAABBFromVertices(vertices);

			}

			List<Point3d> projectedVertices = ProjectVertices(vertices, centroid, array);

			return CalculateProjectedDimensions(projectedVertices);

		}

		catch

		{

			return CalculateAABBFromVertices(vertices);

		}

	}



	private Point3d CalculateCentroid(List<Point3d> vertices)

	{

		double num = vertices.Sum((Point3d v) => v.X);

		double num2 = vertices.Sum((Point3d v) => v.Y);

		double num3 = vertices.Sum((Point3d v) => v.Z);

		int count = vertices.Count;

		return new Point3d(num / (double)count, num2 / (double)count, num3 / (double)count);

	}



	private double[,] CalculateCovarianceMatrix(List<Point3d> vertices, Point3d centroid)

	{

		double[,] array = new double[3, 3];

		int count = vertices.Count;

		foreach (Point3d vertex in vertices)

		{

			Point3d current = vertex;

			double num = current.X - centroid.X;

			double num2 = current.Y - centroid.Y;

			double num3 = current.Z - centroid.Z;

			array[0, 0] += num * num;

			array[0, 1] += num * num2;

			array[0, 2] += num * num3;

			array[1, 0] += num2 * num;

			array[1, 1] += num2 * num2;

			array[1, 2] += num2 * num3;

			array[2, 0] += num3 * num;

			array[2, 1] += num3 * num2;

			array[2, 2] += num3 * num3;

		}

		for (int i = 0; i < 3; i++)

		{

			for (int j = 0; j < 3; j++)

			{

				array[i, j] /= count - 1;

			}

		}

		return array;

	}



	private Vector3d[] CalculateEigenVectors(double[,] matrix)

	{

		try

		{

			Vector3d[] array = (Vector3d[])(object)new Vector3d[3];

			array[0] = PowerIteration(matrix, 50);

			array[1] = FindPerpendicularVector(array[0]);

			array[2] = array[0].CrossProduct(array[1]);

			for (int i = 0; i < 3; i++)

			{

				array[i] = array[i].GetNormal();

			}

			return array;

		}

		catch

		{

			return null;

		}

	}



	private Vector3d PowerIteration(double[,] matrix, int maxIterations)

	{

		Vector3d val = new Vector3d(1.0, 1.0, 1.0);

		Vector3d normal = val.GetNormal();

		for (int i = 0; i < maxIterations; i++)

		{

			double num = matrix[0, 0] * normal.X + matrix[0, 1] * normal.Y + matrix[0, 2] * normal.Z;

			double num2 = matrix[1, 0] * normal.X + matrix[1, 1] * normal.Y + matrix[1, 2] * normal.Z;

			double num3 = matrix[2, 0] * normal.X + matrix[2, 1] * normal.Y + matrix[2, 2] * normal.Z;

			normal = new Vector3d(num, num2, num3);

			if (normal.Length > 1E-10)

			{

				normal = normal.GetNormal();

				continue;

			}

			break;

		}

		return normal;

	}



	private Vector3d FindPerpendicularVector(Vector3d v)

	{

		Vector3d val = ((Math.Abs(v.X) < 0.9) ? Vector3d.XAxis : Vector3d.YAxis);

		Vector3d val2 = v.CrossProduct(val);

		return (val2.Length > 1E-10) ? val2.GetNormal() : Vector3d.ZAxis;

	}



	private List<Vector3d> CalculateSimplifiedPrincipalDirections(List<Point3d> vertices, Point3d centroid)

	{

		try

		{

			double num = 0.0;

			double num2 = 0.0;

			double num3 = 0.0;

			double num4 = 0.0;

			double num5 = 0.0;

			double num6 = 0.0;

			foreach (Point3d vertex in vertices)

			{

				Point3d current = vertex;

				double num7 = current.X - centroid.X;

				double num8 = current.Y - centroid.Y;

				double num9 = current.Z - centroid.Z;

				num += num7 * num7;

				num2 += num8 * num8;

				num3 += num9 * num9;

				num4 += num7 * num8;

				num5 += num7 * num9;

				num6 += num8 * num9;

			}

			int count = vertices.Count;

			num /= (double)count;

			num2 /= (double)count;

			num3 /= (double)count;

			num4 /= (double)count;

			num5 /= (double)count;

			num6 /= (double)count;

			List<Vector3d> list = CalculateApproximateEigenVectors(num, num2, num3, num4, num5, num6);

			if (ValidateDirections(list))

			{

				return list;

			}

			return CalculateFallbackDirections(num, num2, num3);

		}

		catch

		{

			return new List<Vector3d>

			{

				Vector3d.XAxis,

				Vector3d.YAxis,

				Vector3d.ZAxis

			};

		}

	}



	private List<Vector3d> CalculateApproximateEigenVectors(double varX, double varY, double varZ, double covXY, double covXZ, double covYZ)

	{

		List<Vector3d> list = new List<Vector3d>();

		double num = Math.Max(Math.Max(Math.Abs(covXY), Math.Abs(covXZ)), Math.Abs(covYZ));

		double num2 = Math.Max(Math.Max(varX, varY), varZ);

		Vector3d val = ((!(num < num2 * 0.1)) ? CalculateFirstPrincipalDirection(varX, varY, varZ, covXY, covXZ, covYZ) : ((varX >= varY && varX >= varZ) ? Vector3d.XAxis : ((!(varY >= varZ)) ? Vector3d.ZAxis : Vector3d.YAxis)));

		list.Add(val);

		Vector3d val2 = CalculateSecondPrincipalDirection(val, varX, varY, varZ, covXY, covXZ, covYZ);

		list.Add(val2);

		Vector3d val3 = val.CrossProduct(val2);

		Vector3d normal = val3.GetNormal();

		list.Add(normal);

		return list;

	}



	private Vector3d CalculateFirstPrincipalDirection(double varX, double varY, double varZ, double covXY, double covXZ, double covYZ)

	{

		Vector3d val = new Vector3d(1.0, 1.0, 1.0);

		Vector3d result = val.GetNormal();

		Vector3d normal = default(Vector3d);

		for (int i = 0; i < 10; i++)

		{

			double num = varX * result.X + covXY * result.Y + covXZ * result.Z;

			double num2 = covXY * result.X + varY * result.Y + covYZ * result.Z;

			double num3 = covXZ * result.X + covYZ * result.Y + varZ * result.Z;

			normal = new Vector3d(num, num2, num3);

			if (normal.Length > 1E-10)

			{

				normal = normal.GetNormal();

				if (Math.Abs(result.DotProduct(normal)) > 0.999)

				{

					break;

				}

				result = normal;

				continue;

			}

			break;

		}

		return result;

	}



	private Vector3d CalculateSecondPrincipalDirection(Vector3d firstDir, double varX, double varY, double varZ, double covXY, double covXZ, double covYZ)

	{

		Vector3d val = ((!(Math.Abs(firstDir.X) < 0.9)) ? Vector3d.YAxis : Vector3d.XAxis);

		Vector3d val2 = val - firstDir * val.DotProduct(firstDir);

		val = val2.GetNormal();

		val2 = firstDir.CrossProduct(val);

		Vector3d normal = val2.GetNormal();

		double num = ProjectCovarianceMatrix(val, varX, varY, varZ, covXY, covXZ, covYZ);

		double num2 = ProjectCovarianceMatrix(normal, varX, varY, varZ, covXY, covXZ, covYZ);

		double num3 = ProjectCovarianceCross(val, normal, varX, varY, varZ, covXY, covXZ, covYZ);

		double num4 = num + num2;

		double num5 = num * num2 - num3 * num3;

		double num6 = (num4 + Math.Sqrt(num4 * num4 - 4.0 * num5)) / 2.0;

		if (Math.Abs(num3) > 1E-10)

		{

			double num7 = (num6 - num2) / num3;

			val2 = val + normal * num7;

			return val2.GetNormal();

		}

		return (num > num2) ? val : normal;

	}



	private double ProjectCovarianceMatrix(Vector3d direction, double varX, double varY, double varZ, double covXY, double covXZ, double covYZ)

	{

		double x = direction.X;

		double y = direction.Y;

		double z = direction.Z;

		return varX * x * x + varY * y * y + varZ * z * z + 2.0 * (covXY * x * y + covXZ * x * z + covYZ * y * z);

	}



	private double ProjectCovarianceCross(Vector3d dir1, Vector3d dir2, double varX, double varY, double varZ, double covXY, double covXZ, double covYZ)

	{

		double x = dir1.X;

		double y = dir1.Y;

		double z = dir1.Z;

		double x2 = dir2.X;

		double y2 = dir2.Y;

		double z2 = dir2.Z;

		return varX * x * x2 + varY * y * y2 + varZ * z * z2 + covXY * (x * y2 + y * x2) + covXZ * (x * z2 + z * x2) + covYZ * (y * z2 + z * y2);

	}



	private bool ValidateDirections(List<Vector3d> directions)

	{

		if (directions == null || directions.Count != 3)

		{

			return false;

		}

		foreach (Vector3d direction in directions)

		{

			Vector3d current = direction;

			if (Math.Abs(current.Length - 1.0) > 0.01)

			{

				return false;

			}

		}

		Vector3d val = directions[0];

		double num = Math.Abs(val.DotProduct(directions[1]));

		val = directions[0];

		double num2 = Math.Abs(val.DotProduct(directions[2]));

		val = directions[1];

		double num3 = Math.Abs(val.DotProduct(directions[2]));

		return num < 0.1 && num2 < 0.1 && num3 < 0.1;

	}



	private List<Vector3d> CalculateFallbackDirections(double varX, double varY, double varZ)

	{

		List<Vector3d> list = new List<Vector3d>();

		var array = new[]

		{

			new

			{

				Variance = varX,

				Direction = Vector3d.XAxis

			},

			new

			{

				Variance = varY,

				Direction = Vector3d.YAxis

			},

			new

			{

				Variance = varZ,

				Direction = Vector3d.ZAxis

			}

		}.OrderByDescending(v => v.Variance).ToArray();

		list.Add(array[0].Direction);

		list.Add(array[1].Direction);

		list.Add(array[2].Direction);

		return list;

	}



	private double[] CalculateAABBDimensions(Solid3d solid)

	{

		try

		{

			Extents3d geometricExtents = ((Entity)solid).GeometricExtents;

			double[] array = new double[3];

			Point3d val = geometricExtents.MaxPoint;

			double x = val.X;

			val = geometricExtents.MinPoint;

			array[0] = Math.Round(Math.Abs(x - val.X), 2);

			val = geometricExtents.MaxPoint;

			double y = val.Y;

			val = geometricExtents.MinPoint;

			array[1] = Math.Round(Math.Abs(y - val.Y), 2);

			val = geometricExtents.MaxPoint;

			double z = val.Z;

			val = geometricExtents.MinPoint;

			array[2] = Math.Round(Math.Abs(z - val.Z), 2);

			return array;

		}

		catch

		{

			return null;

		}

	}

    private static void CalcAABB(Solid3d solid, out double length, out double width, out double thickness)
    {
        Extents3d ext = ((Entity)solid).GeometricExtents;
        double sx = Math.Abs(ext.MaxPoint.X - ext.MinPoint.X), sy = Math.Abs(ext.MaxPoint.Y - ext.MinPoint.Y), sz = Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z);
        double[] s = { sx, sy, sz }; Array.Sort(s);
        thickness = s[0]; width = s[1]; length = s[2];
    }
    private static void CalculateUnfoldedDimensions(Solid3d solid, ref double sx, ref double sy, ref double sz) { }
    public static bool TryRebuildRectangularPanelKeepingCenter(Solid3d s, BlockTableRecord b, Transaction t, double l, double w, double h, TextureDirection d, out ObjectId id) { id = ObjectId.Null; return false; }

    [CommandMethod("PAIBAN")] public void PaiBanCommand() => PaiBanCommandService.Run();

    [CommandMethod("BZBJ")]
    public void AlignPanelsToXY()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var ed = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            SelectionSet sel = null;
            var implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                sel = implied.Value;
            }
            else
            {
                var so = new PromptSelectionOptions
                {
                    MessageForAdding = "\n请选择要摆正的板件(可框选/多选): ",
                    MessageForRemoval = "\n移除选择: "
                };
                var sr = ed.GetSelection(so, new SelectionFilter(new[] { new TypedValue(0, "3DSOLID") }));
                if (sr.Status != PromptStatus.OK || sr.Value == null || sr.Value.Count == 0)
                {
                    ed.WriteMessage("\n操作已取消。");
                    return;
                }
                sel = sr.Value;
            }

            using var tr = db.TransactionManager.StartTransaction();
            int total = 0, ok = 0, invalid = 0;
            double maxErr = 0;
            double maxAbsRot = 0;
            double sumAbsRot = 0;

            foreach (SelectedObject picked in sel)
            {
                if (picked == null) continue;
                total++;

                if (!(tr.GetObject(picked.ObjectId, OpenMode.ForWrite, false) is Solid3d solid) || solid.IsErased)
                    continue;

                try
                {
                    var verts = GetRealVerticesFromSolid(solid);
                    if (verts == null || verts.Count < 4) { invalid++; continue; }
                    verts = UniquePoints(verts);
                    if (verts.Count < 4) { invalid++; continue; }

                    Point3d center = CalculateCentroid(verts);
                    if (!TryGetPanelAxesFromVertices(verts, out var axisLong, out var axisMid, out var axisThick))
                    {
                        invalid++;
                        continue;
                    }

                    if (!RotateAxisToWorldZ(solid, center, axisThick, 1e-9))
                    {
                        invalid++;
                        continue;
                    }

                    var vertsAfter = UniquePoints(GetRealVerticesFromSolid(solid));
                    if (vertsAfter == null || vertsAfter.Count < 4) { invalid++; continue; }
                    Point3d centerAfter = CalculateCentroid(vertsAfter);
                    if (TryGetPanelAxesFromVertices(vertsAfter, out var long2, out _, out _))
                    {
                        // 1) 按XY投影最长边计算最小旋转角（仅绕Z轴）
                        double rotDeg = AlignPanelLongestEdgeToWorldXByMinimalZRotation(solid, centerAfter, vertsAfter);
                        maxAbsRot = Math.Max(maxAbsRot, Math.Abs(rotDeg));
                        sumAbsRot += Math.Abs(rotDeg);

                        // 2) 重新计算对齐误差
                        var vertsFinal = UniquePoints(GetRealVerticesFromSolid(solid));
                        if (vertsFinal != null && vertsFinal.Count >= 4 &&
                            TryGetPanelAxesFromVertices(vertsFinal, out var longFinal, out _, out _))
                        {
                            maxErr = Math.Max(maxErr, ComputeInPlaneErrorDeg(longFinal));
                        }
                    }

                    ok++;
                }
                catch
                {
                    invalid++;
                }
            }

            tr.Commit();
            double avgAbsRot = ok > 0 ? sumAbsRot / ok : 0;
            ed.WriteMessage($"\n摆正完成: 成功 {ok}/{total}, 无效 {invalid}, 最大平面内误差 {maxErr:F4}°");
            ed.WriteMessage($"\n平面内最长边对齐: 平均旋转 {avgAbsRot:F4}°，最大旋转 {maxAbsRot:F4}°（仅Z轴）");
        }
        catch (Exception ex)
        {
            ed.WriteMessage("\n执行 BZBJ 命令时发生错误: " + ex.Message);
        }
    }

    private bool TryGetPanelAxesFromVertices(List<Point3d> vertices, out Vector3d longAxis, out Vector3d midAxis, out Vector3d thickAxis)
    {
        longAxis = Vector3d.XAxis;
        midAxis = Vector3d.YAxis;
        thickAxis = Vector3d.ZAxis;
        if (vertices == null || vertices.Count < 4) return false;

        var centroid = CalculateCentroid(vertices);
        var dirs = CalculateSimplifiedPrincipalDirections(vertices, centroid);
        if (dirs == null || dirs.Count < 3) return false;

        var axes = new[] { dirs[0].GetNormal(), dirs[1].GetNormal(), dirs[2].GetNormal() };
        var ranges = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var p in vertices)
            {
                double t = p.GetAsVector().DotProduct(axes[i]);
                if (t < min) min = t;
                if (t > max) max = t;
            }
            ranges[i] = max - min;
        }

        int iThick = 0, iLong = 0;
        for (int i = 1; i < 3; i++)
        {
            if (ranges[i] < ranges[iThick]) iThick = i;
            if (ranges[i] > ranges[iLong]) iLong = i;
        }
        int iMid = 3 - iThick - iLong;

        thickAxis = axes[iThick].GetNormal();
        longAxis = axes[iLong].GetNormal();
        midAxis = axes[iMid].GetNormal();

        if (longAxis.CrossProduct(midAxis).DotProduct(thickAxis) < 0)
            midAxis = midAxis.Negate();
        return true;
    }

    private static bool RotateAxisToWorldZ(Entity ent, Point3d center, Vector3d axis, double tol)
    {
        Vector3d n = axis.GetNormal();
        double dot = Math.Max(-1.0, Math.Min(1.0, n.DotProduct(Vector3d.ZAxis)));
        if (Math.Abs(dot - 1.0) < tol) return true;

        if (Math.Abs(dot + 1.0) < tol)
        {
            ent.TransformBy(Matrix3d.Rotation(Math.PI, Vector3d.XAxis, center));
            return true;
        }

        Vector3d rotAxis = n.CrossProduct(Vector3d.ZAxis);
        if (rotAxis.Length < tol) return false;
        double ang = n.GetAngleTo(Vector3d.ZAxis, rotAxis);
        ent.TransformBy(Matrix3d.Rotation(ang, rotAxis.GetNormal(), center));
        return true;
    }

    private static void AlignProjectedAxisToWorldX(Entity ent, Point3d center, Vector3d axis)
    {
        Vector3d v = new Vector3d(axis.X, axis.Y, 0);
        if (v.Length < 1e-9) return;
        v = v.GetNormal();
        double ang = Math.Atan2(v.Y, v.X);

        // 只做最小旋转，避免在平面内多转导致用户感知“跑位”
        if (ang > Math.PI / 2) ang -= Math.PI;
        if (ang < -Math.PI / 2) ang += Math.PI;
        ent.TransformBy(Matrix3d.Rotation(-ang, Vector3d.ZAxis, center));
    }

    private static double AlignPanelLongestEdgeToWorldXByMinimalZRotation(Entity ent, Point3d center, List<Point3d> vertices)
    {
        if (ent == null || vertices == null || vertices.Count < 2) return 0;

        var pts2 = vertices.Select(v => new BzbjAlignmentMath.Pt2(v.X, v.Y)).ToList();
        if (!BzbjAlignmentMath.TryGetLongestEdgeDirection(pts2, out var dir, out _))
            return 0;

        double rotDeg = BzbjAlignmentMath.ComputeMinimalRotationToXAxisDeg(dir);
        double rad = rotDeg * Math.PI / 180.0;
        if (Math.Abs(rad) > 1e-12)
            ent.TransformBy(Matrix3d.Rotation(rad, Vector3d.ZAxis, center));
        return rotDeg;
    }

    private static double ComputeInPlaneErrorDeg(Vector3d axis)
    {
        Vector3d v = new Vector3d(axis.X, axis.Y, 0);
        if (v.Length < 1e-9) return 90.0;
        v = v.GetNormal();
        double ax = Math.Abs(v.DotProduct(Vector3d.XAxis));
        double ay = Math.Abs(v.DotProduct(Vector3d.YAxis));
        double best = Math.Max(ax, ay);
        best = Math.Max(-1.0, Math.Min(1.0, best));
        return Math.Acos(best) * 180.0 / Math.PI;
    }

    private static double GetAxisMin(string axis, Extents3d ex) => axis == "Y" ? ex.MinPoint.Y : axis == "Z" ? ex.MinPoint.Z : ex.MinPoint.X;
    private static double GetAxisMax(string axis, Extents3d ex) => axis == "Y" ? ex.MaxPoint.Y : axis == "Z" ? ex.MaxPoint.Z : ex.MaxPoint.X;

    private static bool IsOnActiveSide(double delta, double baseCoord, double minA, double maxA)
    {
        if (delta > 0) return minA >= baseCoord - 1e-6;
        return maxA <= baseCoord + 1e-6;
    }

    private static bool OverlapsRange(double aMin, double aMax, double bMin, double bMax)
    {
        return !(aMax < bMin + 1e-6 || bMax < aMin + 1e-6);
    }

    private static void GetOtherAxisRanges(string axis, Extents3d ex, out double uMin, out double uMax, out double vMin, out double vMax)
    {
        if (axis == "X")
        {
            uMin = ex.MinPoint.Y; uMax = ex.MaxPoint.Y;
            vMin = ex.MinPoint.Z; vMax = ex.MaxPoint.Z;
            return;
        }
        if (axis == "Y")
        {
            uMin = ex.MinPoint.X; uMax = ex.MaxPoint.X;
            vMin = ex.MinPoint.Z; vMax = ex.MaxPoint.Z;
            return;
        }
        uMin = ex.MinPoint.X; uMax = ex.MaxPoint.X;
        vMin = ex.MinPoint.Y; vMax = ex.MaxPoint.Y;
    }

    /// <summary>
    /// AXSORTHO（旧 AXS）—— 关键字驱动的正交拖拽拉伸（保留给老脚本与工具栏）。
    /// 新的 GUI 拉伸入口请使用 <c>AXS</c>（见 <c>Commands/AxsCommand.cs</c>）。
    /// </summary>
    [CommandMethod("AXSORTHO")]
    public void AxisStretchCommand()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;
        var database = doc.Database;
        RestorePreviousHighlight(database);

        ViewTableRecord oldView = null;
        int oldOrtho = 0;
        string viewName = "俯视图";

        try
        {
            try { oldView = editor.GetCurrentView(); } catch { }
            try
            {
                object ortho = Application.GetSystemVariable("ORTHOMODE");
                if (ortho is short s) oldOrtho = s;
                else if (ortho is int i) oldOrtho = i;
                else if (ortho != null) int.TryParse(ortho.ToString(), out oldOrtho);
            }
            catch { oldOrtho = 0; }

            var vk = new PromptKeywordOptions("\n选择视图 [Top(俯视)/Side(侧视)/Front(正视)] <Top>: ")
            {
                AllowNone = true
            };
            vk.Keywords.Add("Top");
            vk.Keywords.Add("Side");
            vk.Keywords.Add("Front");
            vk.Keywords.Default = "Top";

            var vkRes = editor.GetKeywords(vk);
            if (vkRes.Status == PromptStatus.Cancel) return;

            string choice = string.IsNullOrWhiteSpace(vkRes.StringResult) ? "Top" : vkRes.StringResult;
            viewName = choice == "Side" ? "侧视图" : (choice == "Front" ? "正视图" : "俯视图");
            ApplyView(viewName);
            Application.SetSystemVariable("ORTHOMODE", 1);

            SelectionSet sel = null;
            var implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                sel = implied.Value;
            }
            else
            {
                var so = new PromptSelectionOptions
                {
                    MessageForAdding = "\n请选择要拉伸的板件(可框选/多选): ",
                    MessageForRemoval = "\n移除选择: "
                };
                var sr = editor.GetSelection(so);
                if (sr.Status != PromptStatus.OK || sr.Value == null || sr.Value.Count == 0)
                {
                    editor.WriteMessage("\n操作已取消。");
                    return;
                }
                sel = sr.Value;
            }

            var planeOpt = new PromptKeywordOptions("\n选择拉伸平面 [XY/ZX/YZ] <XY>: ")
            {
                AllowNone = true
            };
            planeOpt.Keywords.Add("XY");
            planeOpt.Keywords.Add("ZX");
            planeOpt.Keywords.Add("YZ");
            planeOpt.Keywords.Default = "XY";
            var planeRes = editor.GetKeywords(planeOpt);
            if (planeRes.Status == PromptStatus.Cancel) return;
            string plane = planeRes.Status == PromptStatus.OK ? planeRes.StringResult : "XY";

            var axisOpt = new PromptKeywordOptions(
                plane == "XY"
                    ? "\n选择拉伸方向 [X/Y] <X>: "
                    : (plane == "ZX" ? "\n选择拉伸方向 [X/Z] <X>: " : "\n选择拉伸方向 [Y/Z] <Y>: "))
            {
                AllowNone = true
            };
            if (plane == "XY")
            {
                axisOpt.Keywords.Add("X");
                axisOpt.Keywords.Add("Y");
                axisOpt.Keywords.Default = "X";
            }
            else if (plane == "ZX")
            {
                axisOpt.Keywords.Add("X");
                axisOpt.Keywords.Add("Z");
                axisOpt.Keywords.Default = "X";
            }
            else
            {
                axisOpt.Keywords.Add("Y");
                axisOpt.Keywords.Add("Z");
                axisOpt.Keywords.Default = "Y";
            }
            var axisRes = editor.GetKeywords(axisOpt);
            if (axisRes.Status == PromptStatus.Cancel) return;
            string axisName = axisRes.Status == PromptStatus.OK ? axisRes.StringResult : axisOpt.Keywords.Default;

            var p1 = editor.GetPoint(new PromptPointOptions("\n指定基准点(放在要移动的那一侧): "));
            if (p1.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n操作已取消。");
                return;
            }

            var p2Opt = new PromptPointOptions("\n指定第二点(正交方向确定拉伸量): ")
            {
                BasePoint = p1.Value,
                UseBasePoint = true
            };
            var p2 = editor.GetPoint(p2Opt);
            if (p2.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n操作已取消。");
                return;
            }

            var relOpt = new PromptKeywordOptions("\n是否同步移动未选中关联板件 [Yes/No] <Yes>: ")
            {
                AllowNone = true
            };
            relOpt.Keywords.Add("Yes");
            relOpt.Keywords.Add("No");
            relOpt.Keywords.Default = "Yes";
            var relRes = editor.GetKeywords(relOpt);
            if (relRes.Status == PromptStatus.Cancel) return;
            bool moveRelated = relRes.Status != PromptStatus.OK || relRes.StringResult == "Yes";

            Point3d basePt = p1.Value;
            Vector3d dragVec = p2.Value - p1.Value;
            Vector3d worldAxis = axisName == "Y" ? Vector3d.YAxis : axisName == "Z" ? Vector3d.ZAxis : Vector3d.XAxis;
            double axisMove = axisName == "Y" ? dragVec.Y : axisName == "Z" ? dragVec.Z : dragVec.X;
            double baseCoord = axisName == "Y" ? basePt.Y : axisName == "Z" ? basePt.Z : basePt.X;

            if (Math.Abs(axisMove) < 1e-6)
            {
                editor.WriteMessage("\n两点在轴向上的位移过小，已取消。");
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int total = 0, ok = 0, nonPanel = 0, locked = 0, failed = 0;
                int movedRelated = 0;
                var selectedIds = new HashSet<ObjectId>();
                foreach (SelectedObject s in sel)
                {
                    if (s != null && !s.ObjectId.IsNull) selectedIds.Add(s.ObjectId);
                }

                double selUmin = double.MaxValue, selUmax = double.MinValue;
                double selVmin = double.MaxValue, selVmax = double.MinValue;

                foreach (SelectedObject picked in sel)
                {
                    if (picked == null) continue;
                    total++;

                    if (!(tr.GetObject(picked.ObjectId, OpenMode.ForWrite, false) is Solid3d solid) || solid.IsErased)
                        continue;

                    PanelInfo panelInfo;
                    try { panelInfo = GetPanelInfo(solid, tr); }
                    catch { panelInfo = null; }

                    if (panelInfo == null) { nonPanel++; continue; }
                    if (panelInfo.IsDimensionLocked) { locked++; continue; }
                    if (panelInfo.CalculationType == PanelCalculationType.ArcPanel ||
                        panelInfo.CalculationType == PanelCalculationType.SplinePanel)
                        { failed++; continue; }

                    try
                    {
                        var verts = GetRealVerticesFromSolid(solid);
                        if (verts == null || verts.Count < 4) { failed++; continue; }
                        verts = UniquePoints(verts);

                        Vector3d axisX, axisY, axisZ;
                        double minX, maxX, minY, maxY, minZ, maxZ;

                        bool frameOk = TryGetOrientedBoxFrame(verts, out axisX, out axisY, out axisZ,
                            out minX, out maxX, out minY, out maxY, out minZ, out maxZ);

                        if (!frameOk)
                        {
                            Point3d centroid = CalculateCentroid(verts);
                            List<Vector3d> dirs = CalculateSimplifiedPrincipalDirections(verts, centroid);
                            if (dirs == null || dirs.Count < 2) { failed++; continue; }

                            axisX = dirs[0].GetNormal();
                            axisY = dirs[1].GetNormal();
                            Vector3d zTry = axisX.CrossProduct(axisY);
                            if (zTry.Length <= 1e-9)
                            {
                                axisZ = (dirs.Count >= 3 ? dirs[2] : Vector3d.ZAxis).GetNormal();
                                axisY = axisZ.CrossProduct(axisX);
                                if (axisY.Length <= 1e-9) { failed++; continue; }
                                axisY = axisY.GetNormal();
                            }
                            else
                            {
                                axisZ = zTry.GetNormal();
                                axisY = axisZ.CrossProduct(axisX).GetNormal();
                            }

                            if (!TryGetProjectedExtents(verts, axisX, axisY, axisZ,
                                out minX, out maxX, out minY, out maxY, out minZ, out maxZ))
                            {
                                failed++;
                                continue;
                            }
                        }

                        // 未选中实体同步移动需要的二维包络（非拉伸轴两个方向）
                        var ge = solid.GeometricExtents;
                        GetOtherAxisRanges(axisName, ge, out var uMin, out var uMax, out var vMin, out var vMax);
                        selUmin = Math.Min(selUmin, uMin);
                        selUmax = Math.Max(selUmax, uMax);
                        selVmin = Math.Min(selVmin, vMin);
                        selVmax = Math.Max(selVmax, vMax);

                        // AXS 拉伸方向强制跟随用户指定轴（支持 XY/ZX/YZ）
                        double dotX = Math.Abs(axisX.DotProduct(worldAxis));
                        double dotY = Math.Abs(axisY.DotProduct(worldAxis));
                        double dotZ = Math.Abs(axisZ.DotProduct(worldAxis));

                        int editAxis = 0;
                        Vector3d targetAxis = axisX;
                        double minA = minX, maxA = maxX;
                        if (dotY >= dotX && dotY >= dotZ)
                        {
                            editAxis = 1; targetAxis = axisY; minA = minY; maxA = maxY;
                        }
                        else if (dotZ >= dotX && dotZ >= dotY)
                        {
                            editAxis = 2; targetAxis = axisZ; minA = minZ; maxA = maxZ;
                        }
                        double delta = axisMove * Math.Sign(targetAxis.DotProduct(worldAxis));

                        if (Math.Abs(delta) < 1e-6) { failed++; continue; }

                        double nMinX = minX, nMaxX = maxX;
                        double nMinY = minY, nMaxY = maxY;
                        double nMinZ = minZ, nMaxZ = maxZ;

                        int thickIndex = 0;
                        double lenX = maxX - minX, lenY = maxY - minY, lenZ = maxZ - minZ;
                        if (lenY <= lenX && lenY <= lenZ) thickIndex = 1;
                        else if (lenZ <= lenX && lenZ <= lenY) thickIndex = 2;

                        // 智能判断：当用户沿厚度轴操作时，优先做整体移动而不是改变厚度（等比约束）
                        if (editAxis == thickIndex)
                        {
                            solid.TransformBy(Matrix3d.Displacement(targetAxis.MultiplyBy(delta)));
                            ok++;
                            continue;
                        }

                        var iv = AxsStretchMath.ComputeIntervalStretch(minA, maxA, baseCoord, delta);
                        if (iv.Action == AxsStretchMath.StretchAction.None) { continue; }

                        switch (editAxis)
                        {
                            case 0:
                                nMinX = iv.NewMin; nMaxX = iv.NewMax;
                                break;
                            case 1:
                                nMinY = iv.NewMin; nMaxY = iv.NewMax;
                                break;
                            default:
                                nMinZ = iv.NewMin; nMaxZ = iv.NewMax;
                                break;
                        }

                        double sx = nMaxX - nMinX, sy = nMaxY - nMinY, sz = nMaxZ - nMinZ;
                        if (sx <= 1e-4 || sy <= 1e-4 || sz <= 1e-4) { failed++; continue; }

                        var ns = new Solid3d();
                        try
                        {
                            ns.CreateBox(sx, sy, sz);
                            ns.TransformBy(Matrix3d.Displacement(new Vector3d(-sx / 2.0, -sy / 2.0, -sz / 2.0)));

                            Vector3d centerLocal = axisX.MultiplyBy((nMinX + nMaxX) / 2.0)
                                + axisY.MultiplyBy((nMinY + nMaxY) / 2.0)
                                + axisZ.MultiplyBy((nMinZ + nMaxZ) / 2.0);
                            Point3d centerWorld = Point3d.Origin + centerLocal;

                            Matrix3d toWorld = Matrix3d.AlignCoordinateSystem(
                                Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                                centerWorld, axisX, axisY, axisZ);
                            ns.TransformBy(toWorld);
                            ns.SetPropertiesFrom(solid);

                            ms.AppendEntity(ns);
                            tr.AddNewlyCreatedDBObject(ns, true);

                            SetPanelInfoFromBoxSizes(panelInfo, sx, sy, sz);
                            panelInfo.EntityId = ns.ObjectId.ToString();
                            SetPanelInfoData(ns, panelInfo, tr);

                            solid.Erase();
                            ok++;
                        }
                        finally
                        {
                            // ns 已加入数据库后由事务管理；这里不Dispose。
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                }

                if (moveRelated && selUmin < selUmax && selVmin < selVmax)
                {
                    foreach (ObjectId id in ms)
                    {
                        if (selectedIds.Contains(id)) continue;
                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Solid3d other) || other.IsErased) continue;

                        PanelInfo otherInfo;
                        try { otherInfo = GetPanelInfo(other, tr); }
                        catch { otherInfo = null; }
                        if (otherInfo == null) continue;

                        var ge = other.GeometricExtents;
                        double amin = GetAxisMin(axisName, ge);
                        double amax = GetAxisMax(axisName, ge);
                        if (!IsOnActiveSide(axisMove, baseCoord, amin, amax)) continue;

                        GetOtherAxisRanges(axisName, ge, out var uMin, out var uMax, out var vMin, out var vMax);
                        if (!OverlapsRange(selUmin, selUmax, uMin, uMax)) continue;
                        if (!OverlapsRange(selVmin, selVmax, vMin, vMax)) continue;

                        other.TransformBy(Matrix3d.Displacement(worldAxis.MultiplyBy(axisMove)));
                        movedRelated++;
                    }
                }

                tr.Commit();
                editor.WriteMessage($"\n轴向拉伸完成: 平面={plane}, 视图={viewName}, 轴={axisName}, 位移={axisMove:F3}mm, 成功 {ok}/{total}, 非板件 {nonPanel}, 锁定 {locked}, 无效 {failed}, 关联位移 {movedRelated}");
            }
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n执行 AXS 命令时发生错误: " + ex.Message);
        }
        finally
        {
            try { Application.SetSystemVariable("ORTHOMODE", oldOrtho); } catch { }
            try
            {
                if (oldView != null)
                {
                    editor.SetCurrentView(oldView);
                    oldView.Dispose();
                }
            }
            catch { }
        }

        void ApplyView(string choice)
        {
            var cv = editor.GetCurrentView();
            try
            {
                cv.ViewTwist = 0.0;
                if (choice == "正视图") cv.ViewDirection = new Vector3d(0, -1, 0);
                else if (choice == "侧视图") cv.ViewDirection = new Vector3d(1, 0, 0);
                else cv.ViewDirection = new Vector3d(0, 0, 1);
                editor.SetCurrentView(cv);
            }
            finally
            {
                cv.Dispose();
            }
        }
    }

    [CommandMethod("CJBJ")]
    public void QuickCreatePanel()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            var win = new QuickCreatePanelWindow();
            var helper = new WindowInteropHelper(win) { Owner = Application.MainWindow.Handle };
            if (win.ShowDialog() != true)
            {
                editor.WriteMessage("\n操作已取消。");
                return;
            }

            var panelData = win.PanelData;
            var selectedFrame = win.SelectedFrame;

            var ppr = editor.GetPoint(new PromptPointOptions("\n请选择板件插入点: "));
            if (ppr.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n操作已取消。");
                return;
            }

            var insert = ppr.Value;
            GetActualDimensions(panelData, out var actualLength, out var actualWidth, out var actualHeight);
            MapDirectionToSolidDimensions(panelData.Direction, actualLength, actualWidth, actualHeight, out var solidX, out var solidY, out var solidZ);

            var spec = new PanelSpecification
            {
                Length = solidX,
                Width = solidY,
                Thickness = solidZ,
                PanelName = GetPanelTypeName(panelData.Direction),
                HorizontalRotation = panelData.HorizontalRotation,
                VerticalRotation = panelData.VerticalRotation
            };

            if (selectedFrame != null)
            {
                spec.OrderId = selectedFrame.OrderId;
                spec.CabinetId = selectedFrame.CabinetId;
                spec.RoomId = selectedFrame.RoomId;
                spec.Material = selectedFrame.DefaultMaterial;
            }

            var newId = PanelCreationService.CreatePanel(spec, insert);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (!(tr.GetObject(newId, OpenMode.ForWrite) is Solid3d solid))
                {
                    tr.Commit();
                    editor.WriteMessage("\n创建成功，但不是3D实体。");
                    return;
                }

                var panelInfo = GetPanelInfo(solid, tr) ?? new PanelInfo();
                panelInfo.PanelName = GetPanelTypeName(panelData.Direction);
                panelInfo.Length = actualLength;
                panelInfo.Width = actualWidth;
                panelInfo.Height = actualHeight;

                if (selectedFrame != null)
                {
                    try
                    {
                        var slot = new PanelSlot
                        {
                            PanelId = Guid.NewGuid().ToString("N"),
                            PanelType = panelInfo.PanelName,
                            Orient = panelData.Direction == PanelDirection.X
                                ? PanelOrient.Horizontal
                                : (panelData.Direction == PanelDirection.Y ? PanelOrient.LeftSide : PanelOrient.Back),
                            Thickness = actualHeight,
                            Material = selectedFrame.DefaultMaterial,
                            ComputedLength = actualLength,
                            ComputedWidth = actualWidth,
                            ComputedThickness = actualHeight,
                            EntityHandle = solid.ObjectId.Handle.Value.ToString(),
                            OrderId = selectedFrame.OrderId,
                            CabinetId = selectedFrame.CabinetId,
                            RoomId = selectedFrame.RoomId,
                            PosX = insert.X - selectedFrame.Origin.X,
                            PosY = insert.Y - selectedFrame.Origin.Y,
                            PosZ = insert.Z - selectedFrame.Origin.Z
                        };

                        selectedFrame.SetDefaultEdgeBanding(slot);
                        selectedFrame.Panels.Add(slot);
                        CabinetFrameService.SaveFrame(selectedFrame);

                        panelInfo.EdgeTop = slot.EdgeTop;
                        panelInfo.EdgeBottom = slot.EdgeBottom;
                        panelInfo.EdgeLeft = slot.EdgeLeft;
                        panelInfo.EdgeRight = slot.EdgeRight;
                    }
                    catch (Exception ex)
                    {
                        editor.WriteMessage("\n注册到外框失败: " + ex.Message);
                    }
                }

                SetPanelInfoData(solid, panelInfo, tr);
                tr.Commit();
            }

            editor.WriteMessage($"\n成功创建 {GetPanelTypeName(panelData.Direction)}，尺寸: {actualLength:F1}×{actualWidth:F1}×{actualHeight:F1}mm");
            editor.WriteMessage($"\n放置轴向: X={solidX:F1}, Y={solidY:F1}, Z={solidZ:F1}（已按{panelData.Direction}方向映射）");
            editor.WriteMessage($"\n插入位置: ({insert.X:F2}, {insert.Y:F2}, {insert.Z:F2})");
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n执行 CJBJ 命令时出错: " + ex.Message);
        }
    }

    private static void GetActualDimensions(QuickCreateData panelData, out double actualLength, out double actualWidth, out double actualHeight)
    {
        actualLength = Math.Max(panelData?.Length ?? 0.0, 1.0);
        actualWidth = Math.Max(panelData?.Width ?? 0.0, 1.0);
        actualHeight = Math.Max(panelData?.Thickness ?? 0.0, 1.0);
    }

    private static string GetPanelTypeName(PanelDirection direction)
    {
        return direction switch
        {
            PanelDirection.X => "顶底板",
            PanelDirection.Y => "侧板",
            PanelDirection.Z => "背板",
            _ => "板件"
        };
    }

    private static void MapDirectionToSolidDimensions(PanelDirection direction, double length, double width, double thickness, out double sx, out double sy, out double sz)
    {
        // X方向: 顶底板 (长X 宽Y 厚Z)
        // Y方向: 侧板   (厚X 宽Y 长Z)
        // Z方向: 背板   (长X 厚Y 宽Z)
        switch (direction)
        {
            case PanelDirection.Y:
                sx = thickness;
                sy = width;
                sz = length;
                break;
            case PanelDirection.Z:
                sx = length;
                sy = thickness;
                sz = width;
                break;
            default:
                sx = length;
                sy = width;
                sz = thickness;
                break;
        }
    }

    private static List<Point3d> UniquePoints(List<Point3d> points, double tol = 1e-3)
    {
        var result = new List<Point3d>();
        if (points == null || points.Count == 0) return result;

        double inv = 1.0 / Math.Max(tol, 1e-9);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in points)
        {
            long qx = (long)Math.Round(p.X * inv);
            long qy = (long)Math.Round(p.Y * inv);
            long qz = (long)Math.Round(p.Z * inv);
            string key = $"{qx}_{qy}_{qz}";
            if (seen.Add(key))
                result.Add(p);
        }
        return result;
    }

    [CommandMethod("FURNITURE_UI")]
    [CommandMethod("ZJM")]
    public void ShowCommandPalette()
    {
        try
        {
            var win = new CommandPalette();
            var helper = new WindowInteropHelper(win) { Owner = Application.MainWindow.Handle };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor?.WriteMessage("\n无法打开命令面板: " + ex.Message);
        }
    }

    [CommandMethod("BOM")]
    public void ExportBom()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var editor = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            var pso = new PromptSelectionOptions { MessageForAdding = "\n请选择要导出BOM的板件(回车=全部): " };
            var sf = new SelectionFilter(new[] { new TypedValue(0, "3DSOLID") });
            var sel = editor.GetSelection(pso, sf);

            var panels = new List<PanelInfo>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                if (sel.Status == PromptStatus.OK && sel.Value != null && sel.Value.Count > 0)
                {
                    foreach (SelectedObject so in sel.Value)
                    {
                        if (so == null) continue;
                        if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is Entity ent)
                        {
                            var info = PanelInfoService.GetPanelInfo(ent, tr);
                            if (info != null) panels.Add(info);
                        }
                    }
                }
                else
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is Solid3d solid)
                        {
                            var info = PanelInfoService.GetPanelInfo(solid, tr);
                            if (info != null) panels.Add(info);
                        }
                    }
                }
                tr.Commit();
            }

            if (panels.Count == 0)
            {
                editor.WriteMessage("\n没有找到可导出的板件信息。");
                return;
            }

            var bom = BomService.GenerateBom(panels);
            var cabinetIds = panels.Where(p => !string.IsNullOrWhiteSpace(p.CabinetId)).Select(p => p.CabinetId).Distinct().ToList();
            var allHardware = HardwareService.GetAllHardware();
            var relatedHardware = allHardware.Where(hw => string.IsNullOrWhiteSpace(hw.CabinetId) || cabinetIds.Contains(hw.CabinetId)).ToList();
            BomService.AddHardwareToBom(bom, relatedHardware);

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV文件|*.csv|合并CSV文件|*.csv",
                DefaultExt = ".csv",
                FileName = $"BOM清单_{DateTime.Now:yyyyMMdd}"
            };
            if (dlg.ShowDialog() != true) return;

            if (dlg.FilterIndex == 2) BomService.ExportMergedBomToCsv(bom, dlg.FileName);
            else BomService.ExportBomToCsv(bom, dlg.FileName);

            editor.WriteMessage("\n================ BOM 已导出 ================");
            editor.WriteMessage("\n文件: " + dlg.FileName);
            editor.WriteMessage($"\n板件: {bom.TotalPanelCount} 块, 总面积: {bom.TotalPanelArea:F2} m²");
            editor.WriteMessage($"\n五金件: {bom.TotalHardwareCount} 件 ({relatedHardware.Count} 种)");
            editor.WriteMessage($"\n总金额: {bom.TotalPrice:F2}");
            editor.WriteMessage("\n============================================");
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n导出 BOM 失败: " + ex.Message);
        }
    }

    [CommandMethod("SUU")]
    public void SubtractUnmerged()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            var a = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n请选择被减实体: " });
            if (a.Status != PromptStatus.OK || a.Value == null || a.Value.Count == 0) return;

            var b = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\n请选择减去实体: " });
            if (b.Status != PromptStatus.OK || b.Value == null || b.Value.Count == 0) return;

            int success = 0;
            int failed = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                bool IsSolidHealthy(Solid3d solid)
                {
                    if (solid == null || solid.IsErased) return false;
                    try
                    {
                        var vol = solid.MassProperties.Volume;
                        if (double.IsNaN(vol) || double.IsInfinity(vol) || vol <= 1e-6) return false;
                        var ext = solid.GeometricExtents;
                        double dx = ext.MaxPoint.X - ext.MinPoint.X;
                        double dy = ext.MaxPoint.Y - ext.MinPoint.Y;
                        double dz = ext.MaxPoint.Z - ext.MinPoint.Z;
                        return dx > 1e-6 && dy > 1e-6 && dz > 1e-6;
                    }
                    catch
                    {
                        return false;
                    }
                }

                var tools = b.Value.GetObjectIds().ToHashSet();
                foreach (var id in a.Value.GetObjectIds())
                {
                    if (!(tr.GetObject(id, OpenMode.ForWrite) is Solid3d target) || target.IsErased) continue;

                    bool changed = false;
                    foreach (var tid in tools)
                    {
                        if (tid == id) continue;
                        if (!(tr.GetObject(tid, OpenMode.ForRead) is Solid3d tool) || tool.IsErased) continue;
                        try
                        {
                            using var toolClone = tool.Clone() as Solid3d;
                            if (toolClone == null) continue;
                            target.BooleanOperation(BooleanOperationType.BoolSubtract, toolClone);
                            if (!IsSolidHealthy(target))
                                throw new InvalidOperationException("BoolSubtract 后实体健康检查失败");
                            changed = true;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            PluginLogger.Warning($"[SUU] BoolSubtract 失败: target={id.Handle}, tool={tid.Handle}, err={ex.Message}");
                        }
                    }
                    if (changed) success++;
                }
                tr.Commit();
            }

            ed.WriteMessage($"\nSUU 完成: 成功处理 {success} 个实体，失败 {failed} 次。");
        }
        catch (Exception ex)
        {
            ed.WriteMessage("\nSUU 执行失败: " + ex.Message);
        }
    }

    [CommandMethod("WKK")]
    public void CreateCabinetFrame()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            var wRes = ed.GetDouble(new PromptDoubleOptions("\n输入柜体宽度(mm): ") { DefaultValue = 800.0 });
            if (wRes.Status != PromptStatus.OK) return;
            var dRes = ed.GetDouble(new PromptDoubleOptions("\n输入柜体总深度(mm): ") { DefaultValue = 600.0 });
            if (dRes.Status != PromptStatus.OK) return;
            var hRes = ed.GetDouble(new PromptDoubleOptions("\n输入柜体高度(mm): ") { DefaultValue = 2400.0 });
            if (hRes.Status != PromptStatus.OK) return;

            var pRes = ed.GetPoint(new PromptPointOptions("\n请选择外框左下后角插入点: "));
            if (pRes.Status != PromptStatus.OK) return;

            var idRes = ed.GetString(new PromptStringOptions("\n输入柜号(如A1): ") { AllowSpaces = true });
            string cabinetId = idRes.Status == PromptStatus.OK ? (idRes.StringResult?.Trim() ?? "A1") : "A1";

            var backOpts = new PromptKeywordOptions("\n背板安装方式 [内嵌(N)/外盖(W)] <N>: ");
            backOpts.Keywords.Add("N");
            backOpts.Keywords.Add("W");
            backOpts.AllowNone = true;
            var backRes = ed.GetKeywords(backOpts);
            var backStyle = (backRes.Status == PromptStatus.OK && backRes.StringResult == "W") ? BackPanelType.Cover : BackPanelType.Insert;

            const double backT = 18.0;
            double outerDepth = Math.Max(dRes.Value, 1.0);
            // 外盖背板：输入的是总深度，内部净深应扣背板厚，避免总深度多算18。
            double frameDepth = backStyle == BackPanelType.Cover ? Math.Max(outerDepth - backT, 1.0) : outerDepth;

            var frame = new CabinetFrame
            {
                Width = Math.Max(wRes.Value, 1.0),
                Depth = frameDepth,
                Height = Math.Max(hRes.Value, 1.0),
                Origin = pRes.Value,
                CabinetId = cabinetId,
                BackPanelStyle = backStyle,
                BackPanelThickness = backT
            };
            frame.InitSpaceTree();
            CabinetFrameService.AddFrame(frame);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var o = pRes.Value;
                double w = frame.Width, d = outerDepth, h = frame.Height;
                var lines = new[]
                {
                    new Line(new Point3d(o.X, o.Y, o.Z), new Point3d(o.X + w, o.Y, o.Z)),
                    new Line(new Point3d(o.X + w, o.Y, o.Z), new Point3d(o.X + w, o.Y + d, o.Z)),
                    new Line(new Point3d(o.X + w, o.Y + d, o.Z), new Point3d(o.X, o.Y + d, o.Z)),
                    new Line(new Point3d(o.X, o.Y + d, o.Z), new Point3d(o.X, o.Y, o.Z)),
                    new Line(new Point3d(o.X, o.Y, o.Z + h), new Point3d(o.X + w, o.Y, o.Z + h)),
                    new Line(new Point3d(o.X + w, o.Y, o.Z + h), new Point3d(o.X + w, o.Y + d, o.Z + h)),
                    new Line(new Point3d(o.X + w, o.Y + d, o.Z + h), new Point3d(o.X, o.Y + d, o.Z + h)),
                    new Line(new Point3d(o.X, o.Y + d, o.Z + h), new Point3d(o.X, o.Y, o.Z + h)),
                    new Line(new Point3d(o.X, o.Y, o.Z), new Point3d(o.X, o.Y, o.Z + h)),
                    new Line(new Point3d(o.X + w, o.Y, o.Z), new Point3d(o.X + w, o.Y, o.Z + h)),
                    new Line(new Point3d(o.X + w, o.Y + d, o.Z), new Point3d(o.X + w, o.Y + d, o.Z + h)),
                    new Line(new Point3d(o.X, o.Y + d, o.Z), new Point3d(o.X, o.Y + d, o.Z + h))
                };
                foreach (var ln in lines)
                {
                    ln.ColorIndex = 3;
                    ln.LinetypeScale = 2.0;
                    ms.AppendEntity(ln);
                    tr.AddNewlyCreatedDBObject(ln, true);
                }

                // 外框锚点: 用于后续检测外框是否被删除，自动清理参数
                var anchor = new DBPoint(o);
                anchor.ColorIndex = 8;
                ms.AppendEntity(anchor);
                tr.AddNewlyCreatedDBObject(anchor, true);
                frame.FrameAnchorHandle = anchor.ObjectId.Handle.Value.ToString();
                tr.Commit();
            }

            ed.WriteMessage($"\nWKK 完成: {frame.Width:F0}×{outerDepth:F0}×{frame.Height:F0}, 柜号={cabinetId}");
            ed.WriteMessage($"\n背板方式: {(backStyle == BackPanelType.Cover ? "外盖" : "内嵌")}，内部净深={frame.Depth:F0}");
            ed.WriteMessage("\n使用 JBJ 命令向外框内添加板件。");
        }
        catch (Exception ex)
        {
            ed.WriteMessage("\n创建外框失败: " + ex.Message);
        }
    }

    [CommandMethod("JBJ")]
    public void AddPanelToFrame()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        try
        {
            CabinetFrameService.LoadAll();
            var mode = new PromptKeywordOptions("\n选择加板模式 [内空(N)/附属(F)] <N>: ");
            mode.Keywords.Add("N");
            mode.Keywords.Add("F");
            mode.AllowNone = true;
            var modeRes = ed.GetKeywords(mode);
            if (modeRes.Status == PromptStatus.Cancel) return;
            if (modeRes.Status == PromptStatus.OK && string.Equals(modeRes.StringResult, "F", StringComparison.OrdinalIgnoreCase))
            {
                AddAttachmentPanelToFrame(ed, db);
                return;
            }

            var pRes = ed.GetPoint(new PromptPointOptions("\n请在外框内点选加板位置: "));
            if (pRes.Status != PromptStatus.OK) return;

            var frame = CabinetFrameService.FindFrameContaining(pRes.Value);
            if (frame == null)
            {
                ed.WriteMessage("\n未找到包含该点的柜体外框，请先执行 WKK。");
                return;
            }

            var hint = frame.GetPanelAddHint(pRes.Value);
            if (hint?.Space == null)
            {
                ed.WriteMessage("\n无法识别可加板空间。");
                return;
            }

            string panelType = hint.PanelType;
            PanelOrient orient = hint.Orient;
            var opt = new PromptKeywordOptions("\n确认板件类型 [默认(Y)/顶板(T)/底板(D)/左侧板(Z)/右侧板(U)/层板(C)/立板(L)/背板(B)/门板(M)] <Y>: ");
            foreach (var k in new[] { "Y", "T", "D", "Z", "U", "C", "L", "B", "M" }) opt.Keywords.Add(k);
            opt.AllowNone = true;
            var optRes = ed.GetKeywords(opt);
            if (optRes.Status == PromptStatus.Cancel) return;
            if (optRes.Status == PromptStatus.OK)
            {
                switch (optRes.StringResult)
                {
                    case "T": panelType = "顶板"; orient = PanelOrient.Horizontal; break;
                    case "D": panelType = "底板"; orient = PanelOrient.Horizontal; break;
                    case "Z": panelType = "左侧板"; orient = PanelOrient.LeftSide; break;
                    case "U": panelType = "右侧板"; orient = PanelOrient.RightSide; break;
                    case "C": panelType = "层板"; orient = PanelOrient.Horizontal; break;
                    case "L": panelType = "立板"; orient = PanelOrient.VerticalDivider; break;
                    case "B": panelType = "背板"; orient = PanelOrient.Back; break;
                    case "M": panelType = "门板"; orient = PanelOrient.Front; break;
                }
            }

            var createdSlots = new List<PanelSlot>();
            double t = Math.Max(frame.DefaultThickness, 1.0);
            double spaceMinX = hint.Space.MinX;
            double spaceMaxX = hint.Space.MaxX;
            double spaceMinY = hint.Space.MinY;
            double spaceMaxY = hint.Space.MaxY;
            double spaceMinZ = hint.Space.MinZ;
            double spaceMaxZ = hint.Space.MaxZ;
            double spaceW = Math.Max(spaceMaxX - spaceMinX, 0.0);
            double spaceH = Math.Max(spaceMaxZ - spaceMinZ, 0.0);

            PanelSlot AddOneAt(double pos)
            {
                SpaceNode leaf = hint.Space;
                if (orient == PanelOrient.Horizontal)
                {
                    double probeZ = Math.Min(Math.Max(pos + t * 0.5, spaceMinZ + 1e-4), spaceMaxZ - 1e-4);
                    double probeX = (spaceMinX + spaceMaxX) * 0.5;
                    double probeY = (spaceMinY + spaceMaxY) * 0.5;
                    leaf = frame.RootSpace?.FindLeaf(probeX, probeY, probeZ) ?? hint.Space;
                }
                else if (orient == PanelOrient.VerticalDivider)
                {
                    double probeX = Math.Min(Math.Max(pos + t * 0.5, spaceMinX + 1e-4), spaceMaxX - 1e-4);
                    double probeY = (spaceMinY + spaceMaxY) * 0.5;
                    double probeZ = (spaceMinZ + spaceMaxZ) * 0.5;
                    leaf = frame.RootSpace?.FindLeaf(probeX, probeY, probeZ) ?? hint.Space;
                }

                var slot = frame.AddPanel(panelType, orient, leaf, pos);
                if (slot == null || slot.ComputedLength <= 0.001 || slot.ComputedWidth <= 0.001 || slot.ComputedThickness <= 0.001)
                {
                    if (slot != null) frame.RemovePanel(slot.PanelId);
                    return null;
                }
                return slot;
            }

            if (panelType == "顶板")
            {
                var slot = AddOneAt(spaceMaxZ - t);
                if (slot == null)
                {
                    ed.WriteMessage("\n顶板位置无效，创建失败。");
                    return;
                }
                createdSlots.Add(slot);
            }
            else if (panelType == "底板")
            {
                var slot = AddOneAt(spaceMinZ);
                if (slot == null)
                {
                    ed.WriteMessage("\n底板位置无效，创建失败。");
                    return;
                }
                createdSlots.Add(slot);
            }
            else if (panelType == "层板" || panelType == "立板")
            {
                var countRes = ed.GetInteger(new PromptIntegerOptions("\n输入插入数量 <1>: ")
                {
                    DefaultValue = 1,
                    AllowNone = true,
                    AllowZero = false,
                    AllowNegative = false,
                    LowerLimit = 1
                });
                if (countRes.Status == PromptStatus.Cancel) return;
                int count = countRes.Status == PromptStatus.OK ? countRes.Value : 1;

                string axisName = panelType == "层板" ? "Z(离底)" : "X(离左)";
                var modeOpts = new PromptKeywordOptions($"\n{panelType}定位方式 [等分(E)/尺寸(S)] <S>: ");
                modeOpts.Keywords.Add("E");
                modeOpts.Keywords.Add("S");
                modeOpts.AllowNone = true;
                var posModeRes = ed.GetKeywords(modeOpts);
                if (posModeRes.Status == PromptStatus.Cancel) return;
                bool equalMode = posModeRes.Status == PromptStatus.OK && string.Equals(posModeRes.StringResult, "E", StringComparison.OrdinalIgnoreCase);

                var positions = new List<double>();
                if (equalMode)
                {
                    if (panelType == "层板")
                    {
                        double free = spaceH - count * t;
                        if (free <= 0)
                        {
                            ed.WriteMessage("\n空间高度不足，无法按该数量插入层板。");
                            return;
                        }
                        double gap = free / (count + 1);
                        for (int i = 1; i <= count; i++) positions.Add(spaceMinZ + gap * i + t * (i - 1));
                        ed.WriteMessage($"\n等分定位: {axisName} 尺寸 = {string.Join(", ", positions.Select(p => (p - spaceMinZ).ToString("F1")))} mm");
                    }
                    else
                    {
                        double free = spaceW - count * t;
                        if (free <= 0)
                        {
                            ed.WriteMessage("\n空间宽度不足，无法按该数量插入立板。");
                            return;
                        }
                        double gap = free / (count + 1);
                        for (int i = 1; i <= count; i++) positions.Add(spaceMinX + gap * i + t * (i - 1));
                        ed.WriteMessage($"\n等分定位: {axisName} 尺寸 = {string.Join(", ", positions.Select(p => (p - spaceMinX).ToString("F1")))} mm");
                    }
                }
                else
                {
                    string tip = panelType == "层板"
                        ? $"\n输入{count}个离底尺寸(mm，逗号分隔): "
                        : $"\n输入{count}个离左尺寸(mm，逗号分隔): ";
                    var strRes = ed.GetString(new PromptStringOptions(tip) { AllowSpaces = false });
                    if (strRes.Status != PromptStatus.OK) return;

                    var tokens = (strRes.StringResult ?? string.Empty).Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length != count)
                    {
                        ed.WriteMessage($"\n输入数量与插入数量不一致，要求 {count} 个。");
                        return;
                    }

                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (!double.TryParse(tokens[i], out double d))
                        {
                            ed.WriteMessage($"\n第{i + 1}个尺寸不是有效数字。");
                            return;
                        }

                        double pos = panelType == "层板" ? (spaceMinZ + d) : (spaceMinX + d);
                        if (panelType == "层板")
                        {
                            if (pos < spaceMinZ - 1e-6 || pos + t > spaceMaxZ + 1e-6)
                            {
                                ed.WriteMessage($"\n第{i + 1}个层板尺寸超出空间范围。");
                                return;
                            }
                        }
                        else
                        {
                            if (pos < spaceMinX - 1e-6 || pos + t > spaceMaxX + 1e-6)
                            {
                                ed.WriteMessage($"\n第{i + 1}个立板尺寸超出空间范围。");
                                return;
                            }
                        }
                        positions.Add(pos);
                    }

                    positions = positions.OrderBy(p => p).ToList();
                    ed.WriteMessage($"\n尺寸定位: {axisName} 尺寸 = {string.Join(", ", positions.Select(p => (panelType == "层板" ? p - spaceMinZ : p - spaceMinX).ToString("F1")))} mm");
                }

                foreach (var pos in positions)
                {
                    var slot = AddOneAt(pos);
                    if (slot == null)
                    {
                        ed.WriteMessage("\n部分板件创建失败，已停止后续插入。");
                        break;
                    }
                    createdSlots.Add(slot);
                }

                if (createdSlots.Count == 0)
                {
                    ed.WriteMessage("\n未创建任何板件。");
                    return;
                }
            }
            else
            {
                var slot = frame.AddPanel(panelType, orient, hint.Space);
                if (slot == null || slot.ComputedLength <= 0.001 || slot.ComputedWidth <= 0.001 || slot.ComputedThickness <= 0.001)
                {
                    if (slot != null) frame.RemovePanel(slot.PanelId);
                    ed.WriteMessage("\n板件尺寸无效，创建失败。");
                    return;
                }
                createdSlots.Add(slot);
            }

            foreach (var slot in createdSlots)
                CabinetFrameService.InheritInfoFromNeighbors(frame, slot);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var slot in createdSlots)
                    CreatePanelSolidEntity(frame, slot, ms, tr);
                tr.Commit();
            }

            CabinetFrameService.SaveFrame(frame);
            ed.WriteMessage($"\n已添加 {createdSlots.Count} 块{panelType}。");
            if (createdSlots.Count > 0)
            {
                var s = createdSlots[0];
                ed.WriteMessage($"\n首块尺寸: {s.ComputedLength:F0}×{s.ComputedWidth:F0}×{s.ComputedThickness:F0}");
            }
        }
        catch (Exception ex)
        {
            ed.WriteMessage("\nJBJ 执行失败: " + ex.Message);
        }
    }

    [CommandMethod("GTCD")]
    public void CabinetDecomposeAlias() => DecomposeCabinet();
    [CommandMethod("OBB")]
    public void CalculateOBBDimensions()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        Editor editor = doc.Editor;
        Database database = doc.Database;
        RestorePreviousHighlight(database);

        try
        {
            PromptEntityOptions options = new PromptEntityOptions("\n请选择一个板件实体: ");
            options.SetRejectMessage("\n请选择一个有效的 3D 实体。");
            options.AddAllowedClass(typeof(Solid3d), true);

            PromptEntityResult entity = editor.GetEntity(options);
            if (entity.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n操作已取消。");
                return;
            }

            using Transaction tr = database.TransactionManager.StartTransaction();
            DBObject obj = tr.GetObject(entity.ObjectId, OpenMode.ForRead);
            Solid3d solid = obj as Solid3d;
            if (solid == null)
            {
                editor.WriteMessage("\n选中的不是有效的 3D 实体。");
                return;
            }

            var perfTrace = new ObbPerformanceTrace();
            double[] dims = new MyPlugin().CalculateOBBDimensionsFromSolid(solid, perfTrace);
            if (dims != null && dims.Length == 3)
            {
                Array.Sort(dims, (a, b) => b.CompareTo(a));
                editor.WriteMessage("\n=== OBB尺寸计算结果 ===");
                editor.WriteMessage($"\n长度: {dims[0]:F2}mm");
                editor.WriteMessage($"\n宽度: {dims[1]:F2}mm");
                editor.WriteMessage($"\n厚度: {dims[2]:F2}mm");
                editor.WriteMessage($"\n板件尺寸: {dims[0]:F2} x {dims[1]:F2} x {dims[2]:F2}mm");
                editor.WriteMessage("\n计算方法: 有向包围盒 (OBB)，优先使用边方向推断轴，失败时回退到 PCA。");

                PanelInfo panelInfo = GetPanelInfo(solid);
                if (panelInfo != null)
                {
                    editor.WriteMessage("\n板件名称: " + panelInfo.PanelName);
                    editor.WriteMessage("\n订单号: " + panelInfo.OrderId);
                }

                editor.WriteMessage("\n--- 性能耗时 ---");
                foreach (string line in perfTrace.ToDisplayText().Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                {
                    editor.WriteMessage("\n" + line);
                }
            }
            else
            {
                editor.WriteMessage("\n无法计算 OBB 尺寸。");
                editor.WriteMessage("\n--- 性能耗时 ---");
                foreach (string line in perfTrace.ToDisplayText().Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                {
                    editor.WriteMessage("\n" + line);
                }
            }

            tr.Commit();
        }
        catch (Exception ex)
        {
            editor.WriteMessage("\n计算 OBB 尺寸时发生错误: " + ex.Message);
        }
    }

    [CommandMethod("CZBJ")]
    public void FindPanelByIdOrExtensionData()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;

        var ed = doc.Editor;
        var db = doc.Database;
        RestorePreviousHighlight(db);

        var win = new CzbjQueryWindow { Owner = null };
        win.Show();
    }

    private static void FindSingleByEntityId(Editor ed, Database db)
    {
        var so = new PromptStringOptions("\n请输入实体ID/Handle（支持十进制、十六进制、(12345)）: ")
        {
            AllowSpaces = false
        };
        var sr = ed.GetString(so);
        if (sr.Status != PromptStatus.OK) { ed.WriteMessage("\n操作已取消。"); return; }

        if (!TryParseHandleInput(sr.StringResult, out var handle))
        {
            ed.WriteMessage("\nID格式无效。");
            return;
        }

        if (!db.TryGetObjectId(handle, out var objId))
        {
            ed.WriteMessage("\n未找到该实体。");
            return;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var ent = tr.GetObject(objId, OpenMode.ForRead, false) as Entity;
        if (ent == null || ent.IsErased)
        {
            ed.WriteMessage("\n实体不存在或已删除。");
            return;
        }

        ed.SetImpliedSelection(new[] { objId });
        ZoomToSelection(db, ed, new[] { objId });

        var info = PanelInfoService.GetPanelInfo(ent, tr);
        if (info != null)
        {
            ed.WriteMessage($"\n找到板件: {SafeText(info.PanelName)}");
            ed.WriteMessage($"\n订单号: {SafeText(info.OrderId)}");
            ed.WriteMessage($"\n柜号: {SafeText(info.CabinetId)}");
            ed.WriteMessage($"\n房间: {SafeText(info.RoomId)}");
            ed.WriteMessage($"\n材质: {SafeText(info.Material)}");
            ed.WriteMessage($"\n尺寸: {info.Length:F1}×{info.Width:F1}×{info.Height:F1}mm");
            ed.WriteMessage($"\n备注: {SafeText(info.Remarks)}");
        }
        else
        {
            ed.WriteMessage("\n找到实体，但没有板件扩展字典信息。");
        }

        tr.Commit();
        ed.WriteMessage("\n已选中并定位到该实体。");
    }

    private static void FindByPanelInfoFilter(Editor ed, Database db)
    {
        var fieldOpt = new PromptKeywordOptions("\n筛选字段 [订单号(O)/柜号(C)/房间(R)/板件名(N)/材质(M)/备注(B)/实体ID(E)/全部(A)] <A>: ")
        {
            AllowNone = true
        };
        fieldOpt.Keywords.Add("O");
        fieldOpt.Keywords.Add("C");
        fieldOpt.Keywords.Add("R");
        fieldOpt.Keywords.Add("N");
        fieldOpt.Keywords.Add("M");
        fieldOpt.Keywords.Add("B");
        fieldOpt.Keywords.Add("E");
        fieldOpt.Keywords.Add("A");
        fieldOpt.Keywords.Default = "A";
        var fieldRes = ed.GetKeywords(fieldOpt);
        if (fieldRes.Status == PromptStatus.Cancel) return;
        string field = fieldRes.Status == PromptStatus.OK ? fieldRes.StringResult : "A";

        var modeOpt = new PromptKeywordOptions("\n匹配方式 [包含(P)/精确(X)] <P>: ")
        {
            AllowNone = true
        };
        modeOpt.Keywords.Add("P");
        modeOpt.Keywords.Add("X");
        modeOpt.Keywords.Default = "P";
        var modeRes = ed.GetKeywords(modeOpt);
        if (modeRes.Status == PromptStatus.Cancel) return;
        bool exact = modeRes.Status == PromptStatus.OK && string.Equals(modeRes.StringResult, "X", StringComparison.OrdinalIgnoreCase);

        var valueRes = ed.GetString(new PromptStringOptions("\n请输入筛选值: ") { AllowSpaces = true });
        if (valueRes.Status != PromptStatus.OK) return;
        string keyword = (valueRes.StringResult ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            ed.WriteMessage("\n筛选值不能为空。");
            return;
        }

        var hits = new List<ObjectId>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                if (ent is not Solid3d) continue;
                if (!PanelInfoService.HasPanelInfo(ent, tr)) continue;

                var info = PanelInfoService.GetPanelInfo(ent, tr);
                if (info == null) continue;

                if (IsMatchByField(info, id.Handle.Value.ToString(), field, keyword, exact))
                    hits.Add(id);
            }

            tr.Commit();
        }

        if (hits.Count == 0)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            ed.WriteMessage("\n未找到符合条件的板件。");
            return;
        }

        ed.SetImpliedSelection(hits.ToArray());
        ZoomToSelection(db, ed, hits);
        ed.WriteMessage($"\n筛选完成，匹配到 {hits.Count} 个板件，已自动选中。");
    }

    private static bool IsMatchByField(PanelInfo info, string entityId, string field, string keyword, bool exact)
    {
        bool Hit(string s) => exact
            ? string.Equals((s ?? string.Empty).Trim(), keyword, StringComparison.OrdinalIgnoreCase)
            : (s ?? string.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        return (field ?? "A").ToUpperInvariant() switch
        {
            "O" => Hit(info.OrderId),
            "C" => Hit(info.CabinetId),
            "R" => Hit(info.RoomId),
            "N" => Hit(info.PanelName),
            "M" => Hit(info.Material),
            "B" => Hit(info.Remarks),
            "E" => Hit(entityId) || Hit(info.EntityId),
            _ => Hit(info.OrderId) || Hit(info.CabinetId) || Hit(info.RoomId) || Hit(info.PanelName) ||
                 Hit(info.Material) || Hit(info.Remarks) || Hit(entityId) || Hit(info.EntityId)
        };
    }

    private static bool TryParseHandleInput(string input, out Handle handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string s = input.Trim();
        if (s.StartsWith("(") && s.EndsWith(")") && s.Length > 2)
            s = s.Substring(1, s.Length - 2).Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(2);

        if (long.TryParse(s, out long dec))
        {
            handle = new Handle(dec);
            return true;
        }
        try
        {
            long hex = Convert.ToInt64(s, 16);
            handle = new Handle(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ZoomToSelection(Database db, Editor ed, IEnumerable<ObjectId> ids)
    {
        if (db == null || ed == null || ids == null) return;

        bool has = false;
        Extents3d ex = default;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            foreach (var id in ids)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased) continue;
                try
                {
                    var ge = ent.GeometricExtents;
                    if (!has) { ex = ge; has = true; }
                    else ex.AddExtents(ge);
                }
                catch { }
            }
            tr.Commit();
        }
        if (!has) return;

        try
        {
            var view = ed.GetCurrentView();
            try
            {
                var center = ex.MinPoint + (ex.MaxPoint - ex.MinPoint) * 0.5;
                double width = Math.Max(ex.MaxPoint.X - ex.MinPoint.X, 1.0) * 1.3;
                double height = Math.Max(ex.MaxPoint.Y - ex.MinPoint.Y, 1.0) * 1.3;
                view.CenterPoint = new Point2d(center.X, center.Y);
                view.Width = width;
                view.Height = height;
                ed.SetCurrentView(view);
            }
            finally
            {
                view.Dispose();
            }
        }
        catch { }
    }

    private static string SafeText(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    [CommandMethod("GICD")] public void DecomposeCabinet() { }
    [CommandMethod("ZBBH")] public void AutoNumberPanels() { }
    [CommandMethod("FBGL")] public void ManageEdgeBands() { }
    [CommandMethod("WJJL")] public void ManageHardware() { }
    [CommandMethod("KJ")] public void CreateCabinetFrameCommand() { }

    [CommandMethod("WLXS")]
    public void ShowAllTextureDirections()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var db = doc.Database;
        var ed = doc.Editor;

        var pso = new PromptSelectionOptions
         {
             MessageForAdding = "\n请选择要显示纹理的板件(回车=模型空间全部): "
         };
         var sf = new SelectionFilter(new TypedValue[] { new TypedValue(0, "3DSOLID") });
         var sel = ed.GetSelection(pso, sf);

         if (sel.Status == PromptStatus.Cancel)
         {
             ed.WriteMessage("\n操作已取消。");
             return;
         }

         List<ObjectId> targetIds;
         using (var tr = db.TransactionManager.StartTransaction())
         {
             if (sel.Status == PromptStatus.OK && sel.Value != null && sel.Value.Count > 0)
             {
                 targetIds = new List<ObjectId>();
                 foreach (SelectedObject so in sel.Value)
                 {
                     if (so != null) targetIds.Add(so.ObjectId);
                 }
             }
             else
             {
                 var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                 var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                 targetIds = ms.Cast<ObjectId>().ToList();
             }
             tr.Commit();
         }

        int count = 0;
        int corrected = 0;
        using var tr2 = db.TransactionManager.StartTransaction();
        foreach (var id in targetIds)
        {
            var ent = tr2.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (ent == null || ent.IsErased) continue;
            if (ent is not Solid3d solid) continue;
            if (!PanelInfoService.HasPanelInfo(ent, tr2)) continue;

            var info = PanelInfoService.GetPanelInfo(ent, tr2);
            if (info == null) continue;

            var handle = ent.Handle.Value.ToString();

            if (!info.IsTextureDirectionManual)
            {
                var detected = TextureDirectionService.DetectInitialTextureDirection(solid, info);
                if (detected != info.TextureDirection)
                {
                    info.TextureDirection = detected;
                    var entWrite = tr2.GetObject(id, OpenMode.ForWrite, false);
                    PanelInfoService.SavePanelInfo(entWrite, info, tr2);
                    corrected++;
                }
            }

            TextureDirectionService.RefreshTextureLinesForPanel(db, tr2, solid, info, handle);
            count++;
        }

        TextureDirectionService.SetTextureLayerVisibility(db, tr2, true);
        tr2.Commit();
        if (corrected > 0)
            ed.WriteMessage($"\n纹理显示完毕，已为 {count} 个板件生成纹路线（双箭头样式），其中 {corrected} 个板件的纹理方向已自动纠正。");
        else
            ed.WriteMessage($"\n纹理显示完毕，已为 {count} 个板件生成纹路线（双箭头样式）。");
    }

    [CommandMethod("WLYC")]
    public void HideAllTextureDirections()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var db = doc.Database;
        var ed = doc.Editor;

        int count = 0;
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        foreach (ObjectId id in ms)
        {
            var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            if (ent == null || ent.IsErased) continue;
            if (ent is not Solid3d) continue;
            if (!PanelInfoService.HasPanelInfo(ent, tr)) continue;

            var info = PanelInfoService.GetPanelInfo(ent, tr);
            if (info == null) continue;

            var handle = ent.Handle.Value.ToString();
            TextureDirectionService.RemoveTextureLinesForPanel(db, tr, handle);
            count++;
        }

        tr.Commit();
        ed.WriteMessage($"\n纹理隐藏完毕，已移除 {count} 个板件的纹路线。");
    }

    [CommandMethod("PLXG")]
    public void SetPanelInfoBatch()
    {
        try
        {
            var mdiDoc = Application.DocumentManager.MdiActiveDocument;
            if (mdiDoc == null)
            {
                System.Windows.MessageBox.Show("没有活动的 AutoCAD 文档。", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
                return;
            }
            var db = mdiDoc.Database;
            var editor = mdiDoc.Editor;
            RestorePreviousHighlight(db);

            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\n请选择要批量处理的3D实体板件: ",
                MessageForRemoval = "\n移除选择: "
            };
            var sf = new SelectionFilter(new TypedValue[] { new TypedValue(0, "3DSOLID") });
            var selRes = editor.GetSelection(pso, sf);

            if (selRes.Status != PromptStatus.OK)
            {
                if (selRes.Status == PromptStatus.Cancel)
                    editor.WriteMessage("\n操作已取消。");
                return;
            }

            var objectIds = selRes.Value.GetObjectIds();
            if (objectIds.Length == 0)
            {
                editor.WriteMessage("\n没有选择任何板件。");
                return;
            }

            editor.WriteMessage($"\n已选择 {objectIds.Length} 个3D实体。");

            var batchItems = new List<BatchEditItem>();
            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                foreach (ObjectId objId in objectIds)
                {
                    try
                    {
                        var obj = tr.GetObject(objId, OpenMode.ForRead);
                        if (obj is Solid3d solid)
                        {
                            var panelInfo = GetPanelInfo(obj) ?? new PanelInfo();
                            var item = new BatchEditItem
                            {
                                EntityId = objId.Handle.Value.ToString(),
                                OrderId = panelInfo.OrderId,
                                CabinetId = panelInfo.CabinetId,
                                PanelName = panelInfo.PanelName,
                                Material = panelInfo.Material,
                                Length = panelInfo.Length,
                                Width = panelInfo.Width,
                                Height = panelInfo.Height,
                                EdgeBanding = panelInfo.EdgeBanding,
                                Paint = panelInfo.Paint,
                                IsArcPanel = panelInfo.IsArcPanel
                            };
                            batchItems.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        editor.WriteMessage($"\n读取实体 {objId} 信息时出错: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            catch (Exception ex)
            {
                editor.WriteMessage($"\n事务操作失败: {ex.Message}");
                return;
            }

            if (batchItems.Count == 0)
            {
                editor.WriteMessage("\n没有找到有效的板件信息。");
                return;
            }

            var window = new BatchEditWindow(batchItems);
            var helper = new System.Windows.Interop.WindowInteropHelper(window)
            {
                Owner = Application.MainWindow.Handle
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            var mdiDoc2 = Application.DocumentManager.MdiActiveDocument;
            var editor2 = mdiDoc2?.Editor;
            if (editor2 != null)
                editor2.WriteMessage($"\n执行PLXG命令时出错: {ex.Message}");
            System.Windows.MessageBox.Show($"执行PLXG命令时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
    }

    private void ApplyBatchEdits(List<BatchEditItem> items, Editor editor)
    {
        if (items == null || items.Count == 0) return;

        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var db = doc.Database;

        int success = 0;
        using var tr = db.TransactionManager.StartTransaction();
        try
        {
            foreach (var item in items)
            {
                try
                {
                    var handle = new Handle(Convert.ToInt64(item.EntityId));
                    if (!db.TryGetObjectId(handle, out var objId)) continue;

                    var obj = tr.GetObject(objId, OpenMode.ForWrite);
                    if (obj is Solid3d solid)
                    {
                        var panelInfo = PanelInfoService.GetPanelInfo(obj, tr) ?? new PanelInfo();
                        panelInfo.OrderId = item.OrderId;
                        panelInfo.CabinetId = item.CabinetId;
                        panelInfo.PanelName = item.PanelName;
                        panelInfo.Material = item.Material;
                        panelInfo.Length = item.Length;
                        panelInfo.Width = item.Width;
                        panelInfo.Height = item.Height;
                        panelInfo.EdgeBanding = item.EdgeBanding;
                        panelInfo.Paint = item.Paint;
                        panelInfo.IsArcPanel = item.IsArcPanel;
                        panelInfo.CalculationType = PanelCalculationType.OBB;

                        PanelInfoService.SavePanelInfo(solid, panelInfo, tr);
                        success++;
                    }
                }
                catch (Exception ex)
                {
                    editor?.WriteMessage($"\n保存板件 {item.EntityId} 时出错: {ex.Message}");
                }
            }
            tr.Commit();
            editor?.WriteMessage($"\n批量修改完成！成功: {success} / {items.Count}");
        }
        catch (Exception ex)
        {
            editor?.WriteMessage($"\n批量保存失败: {ex.Message}");
        }
    }

    [CommandMethod("CKBJ")]
    public void ShowPanelInfoList()
    {
        try
        {
            var mdiDoc = Application.DocumentManager.MdiActiveDocument;
            if (mdiDoc == null)
            {
                System.Windows.MessageBox.Show("没有活动的 AutoCAD 文档。", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
                return;
            }
            var db = mdiDoc.Database;
            var editor = mdiDoc.Editor;
            RestorePreviousHighlight(db);

            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\n请选择要查看的3D实体板件: ",
                MessageForRemoval = "\n移除选择: "
            };
            var sf = new SelectionFilter(new TypedValue[] { new TypedValue(0, "3DSOLID") });
            var selRes = editor.GetSelection(pso, sf);

            if (selRes.Status != PromptStatus.OK)
            {
                if (selRes.Status == PromptStatus.Cancel)
                    editor.WriteMessage("\n操作已取消。");
                return;
            }

            var objectIds = selRes.Value.GetObjectIds();
            if (objectIds.Length == 0)
            {
                editor.WriteMessage("\n没有选择任何板件。");
                return;
            }

            editor.WriteMessage($"\n已选择 {objectIds.Length} 个3D实体进行查看。");

            try
            {
                var listWindow = new PanelInfoListWindow(new List<ObjectId>(objectIds));
                var helper = new System.Windows.Interop.WindowInteropHelper(listWindow)
                {
                    Owner = Application.MainWindow.Handle
                };
                listWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                editor.WriteMessage("\n显示板件信息列表窗口时出错: " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            var mdiDoc2 = Application.DocumentManager.MdiActiveDocument;
            var editor2 = mdiDoc2?.Editor;
            if (editor2 != null)
                editor2.WriteMessage($"\n执行CKBJ命令时出错: {ex.Message}");
            System.Windows.MessageBox.Show($"执行CKBJ命令时出错: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Hand);
        }
    }
}
