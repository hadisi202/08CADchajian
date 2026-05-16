namespace FurniturePlugin;

/// <summary>
/// 2D 点的轻量值类型，跨 NestingService / OutlineService /
/// IrregularNestingService / FlatshotProjectionService 共用。
/// </summary>
public struct Pt2d
{
    public double X;
    public double Y;

    public Pt2d(double x, double y)
    {
        X = x;
        Y = y;
    }
}
