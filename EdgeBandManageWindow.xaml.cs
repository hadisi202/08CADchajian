using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;

namespace FurniturePlugin
{
    public partial class EdgeBandManageWindow : Window
    {
        private List<ObjectId> _panelIds;
        private List<PanelInfo> _panelInfos = new();

        private static readonly string[] CommonEdgeBandMaterials = new[]
        {
            "1mm同色封边",
            "2mm同色封边",
            "1mm异色封边",
            "2mm异色封边",
            "0.5mm封边",
            "铝合金封边",
            "实木封边条",
            ""
        };

        public EdgeBandManageWindow(List<ObjectId> panelIds)
        {
            InitializeComponent();
            _panelIds = panelIds ?? new List<ObjectId>();
            InitComboBoxes();
            LoadPanelData();
        }

        private void InitComboBoxes()
        {
            foreach (var cmb in new[] { cmbEdgeTop, cmbEdgeBottom, cmbEdgeLeft, cmbEdgeRight })
            {
                cmb.ItemsSource = CommonEdgeBandMaterials.ToList();
            }
            cmbEdgeTop.SelectedIndex = 0;
            cmbEdgeBottom.SelectedIndex = 0;
            cmbEdgeLeft.SelectedIndex = 6; // 默认空
            cmbEdgeRight.SelectedIndex = 6;
        }

        private void LoadPanelData()
        {
            _panelInfos.Clear();
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var id in _panelIds)
                {
                    try
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null) continue;
                        var info = PanelInfoService.GetPanelInfo(entity, tr);
                        if (info != null)
                        {
                            info.EntityId = id.Handle.Value.ToString();
                            _panelInfos.Add(info);
                        }
                    }
                    catch { }
                }
                tr.Commit();
            }

            dgPanels.ItemsSource = _panelInfos;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            var selected = dgPanels.SelectedItems.Cast<PanelInfo>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选择要应用封边的板件。", "提示");
                return;
            }

            var edgeTop = cmbEdgeTop.Text?.Trim() ?? "";
            var edgeBottom = cmbEdgeBottom.Text?.Trim() ?? "";
            var edgeLeft = cmbEdgeLeft.Text?.Trim() ?? "";
            var edgeRight = cmbEdgeRight.Text?.Trim() ?? "";

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                int updated = 0;
                foreach (var info in selected)
                {
                    try
                    {
                        var handle = new Handle(long.Parse(info.EntityId));
                        var objId = doc.Database.GetObjectId(false, handle, 0);
                        if (objId == ObjectId.Null) continue;

                        var entity = tr.GetObject(objId, OpenMode.ForWrite) as DBObject;
                        if (entity == null) continue;

                        if (chkApplyTop.IsChecked == true) info.EdgeTop = edgeTop;
                        if (chkApplyBottom.IsChecked == true) info.EdgeBottom = edgeBottom;
                        if (chkApplyLeft.IsChecked == true) info.EdgeLeft = edgeLeft;
                        if (chkApplyRight.IsChecked == true) info.EdgeRight = edgeRight;

                        PanelInfoService.SavePanelInfo(entity, info, tr);
                        updated++;
                    }
                    catch { }
                }
                tr.Commit();

                MessageBox.Show($"已更新 {updated} 块板件的封边信息。", "完成");
                dgPanels.Items.Refresh();
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            var selected = dgPanels.SelectedItems.Cast<PanelInfo>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先选择要清除封边的板件。", "提示");
                return;
            }

            foreach (var info in selected)
            {
                info.EdgeTop = info.EdgeBottom = info.EdgeLeft = info.EdgeRight = "";
            }

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var info in selected)
                {
                    try
                    {
                        var handle = new Handle(long.Parse(info.EntityId));
                        var objId = doc.Database.GetObjectId(false, handle, 0);
                        if (objId == ObjectId.Null) continue;
                        var entity = tr.GetObject(objId, OpenMode.ForWrite) as DBObject;
                        if (entity == null) continue;
                        PanelInfoService.SavePanelInfo(entity, info, tr);
                    }
                    catch { }
                }
                tr.Commit();
            }

            dgPanels.Items.Refresh();
            MessageBox.Show("已清除封边信息。", "完成");
        }

        private void BtnAutoSet_Click(object sender, RoutedEventArgs e)
        {
            // 根据板件类型自动设置封边规则
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                int updated = 0;
                foreach (var info in _panelInfos)
                {
                    var name = info.PanelName ?? "";
                    if (string.IsNullOrEmpty(name)) continue;

                    // 侧板：前后封边，上下看情况
                    if (name.Contains("侧板"))
                    {
                        info.EdgeTop = "1mm同色封边";
                        info.EdgeBottom = "1mm同色封边";
                        info.EdgeLeft = "1mm同色封边";
                        info.EdgeRight = "";
                    }
                    // 顶底板：前后封边
                    else if (name.Contains("顶板") || name.Contains("底板"))
                    {
                        info.EdgeTop = "1mm同色封边";
                        info.EdgeBottom = "1mm同色封边";
                        info.EdgeLeft = "";
                        info.EdgeRight = "";
                    }
                    // 层板：前边封边
                    else if (name.Contains("层板"))
                    {
                        info.EdgeTop = "1mm同色封边";
                        info.EdgeBottom = "";
                        info.EdgeLeft = "";
                        info.EdgeRight = "";
                    }
                    // 门板：四边封边
                    else if (name.Contains("门板") || name.Contains("抽屉面板"))
                    {
                        info.EdgeTop = "2mm同色封边";
                        info.EdgeBottom = "2mm同色封边";
                        info.EdgeLeft = "2mm同色封边";
                        info.EdgeRight = "2mm同色封边";
                    }
                    // 背板：不封边
                    else if (name.Contains("背板"))
                    {
                        info.EdgeTop = info.EdgeBottom = info.EdgeLeft = info.EdgeRight = "";
                    }
                    else continue;

                    try
                    {
                        var handle = new Handle(long.Parse(info.EntityId));
                        var objId = doc.Database.GetObjectId(false, handle, 0);
                        if (objId == ObjectId.Null) continue;
                        var entity = tr.GetObject(objId, OpenMode.ForWrite) as DBObject;
                        if (entity == null) continue;
                        PanelInfoService.SavePanelInfo(entity, info, tr);
                        updated++;
                    }
                    catch { }
                }
                tr.Commit();

                dgPanels.Items.Refresh();
                MessageBox.Show($"已按板件类型自动设置 {updated} 块板件的封边。", "完成");
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadPanelData();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
