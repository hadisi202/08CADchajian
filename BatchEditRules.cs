using System.Collections.Generic;

namespace FurniturePlugin
{
    public static class BatchEditRules
    {
        public static Dictionary<string, bool> BuildAutoCheckStates(
            string orderId,
            string cabinetId,
            string roomId,
            string panelName,
            string material,
            string extraLength,
            string extraWidth,
            string extraHeight,
            string edgeTop,
            string edgeBottom,
            string edgeLeft,
            string edgeRight,
            string paint,
            string remarks,
            int panelShapeIndex)
        {
            return new Dictionary<string, bool>
            {
                ["OrderId"] = !string.IsNullOrWhiteSpace(orderId),
                ["CabinetId"] = !string.IsNullOrWhiteSpace(cabinetId),
                ["RoomId"] = !string.IsNullOrWhiteSpace(roomId),
                ["PanelName"] = !string.IsNullOrWhiteSpace(panelName),
                ["Material"] = !string.IsNullOrWhiteSpace(material),
                ["ExtraLength"] = !string.IsNullOrWhiteSpace(extraLength),
                ["ExtraWidth"] = !string.IsNullOrWhiteSpace(extraWidth),
                ["ExtraHeight"] = !string.IsNullOrWhiteSpace(extraHeight),
                ["EdgeTop"] = !string.IsNullOrWhiteSpace(edgeTop),
                ["EdgeBottom"] = !string.IsNullOrWhiteSpace(edgeBottom),
                ["EdgeLeft"] = !string.IsNullOrWhiteSpace(edgeLeft),
                ["EdgeRight"] = !string.IsNullOrWhiteSpace(edgeRight),
                ["Paint"] = !string.IsNullOrWhiteSpace(paint),
                ["Remarks"] = !string.IsNullOrWhiteSpace(remarks),
                ["PanelShape"] = panelShapeIndex > 0
            };
        }

        public static int CalcTypeToBatchShapeIndex(PanelCalculationType calcType)
        {
            // BatchEditWindow.xaml 的 PanelShapeComboBox 仅 4 项：
            //   0=不修改, 1=常规矩形板(AABB), 2=旋转过的矩形板(OBB), 3=异形板
            return calcType switch
            {
                PanelCalculationType.AABB => 1,
                PanelCalculationType.OBB => 2,
                PanelCalculationType.Irregular => 3,
                PanelCalculationType.ArcPanel => 3, // 批量编辑里把弧形板归入异形板组
                PanelCalculationType.SplinePanel => 3,
                _ => 2
            };
        }

        public static PanelCalculationType BatchShapeTextToCalcType(string shapeText)
        {
            if (string.IsNullOrWhiteSpace(shapeText))
                return PanelCalculationType.AABB;

            if (shapeText.StartsWith("常规矩形板"))
                return PanelCalculationType.AABB;

            if (shapeText.StartsWith("圆弧板件"))
                return PanelCalculationType.ArcPanel;

            if (shapeText.StartsWith("样条实体"))
                return PanelCalculationType.SplinePanel;

            if (shapeText.StartsWith("异形板"))
                return PanelCalculationType.Irregular;

            if (shapeText.StartsWith("旋转过的矩形板"))
                return PanelCalculationType.OBB;

            return PanelCalculationType.OBB;
        }
    }
}
