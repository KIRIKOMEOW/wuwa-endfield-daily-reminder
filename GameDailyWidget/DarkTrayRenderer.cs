using System.Drawing;
using System.Windows.Forms;

namespace GameDailyWidget;

internal static class DarkTrayTheme
{
    internal static void Apply(ToolStripDropDown menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = Color.FromArgb(40, 41, 48);
        menu.ForeColor = Color.FromArgb(242, 240, 233);
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = menu.ForeColor;
            if (item is ToolStripMenuItem submenu && submenu.HasDropDownItems) Apply(submenu.DropDown);
        }
    }
    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        internal Renderer() : base(new Colors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(242, 240, 233) : Color.FromArgb(145, 146, 155);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(211, 183, 128);
            base.OnRenderArrow(e);
        }
    }
    private sealed class Colors : ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(40, 41, 48);
        private static readonly Color Selected = Color.FromArgb(67, 68, 79);
        private static readonly Color Border = Color.FromArgb(93, 94, 103);
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Selected;
        public override Color MenuItemPressedGradientMiddle => Selected;
        public override Color MenuItemPressedGradientEnd => Selected;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
        public override Color CheckBackground => Selected;
        public override Color CheckSelectedBackground => Selected;
        public override Color CheckPressedBackground => Selected;
    }
}
