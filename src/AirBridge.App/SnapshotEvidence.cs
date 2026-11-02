using System.Text.Json;

namespace AirBridge.App;

/// <summary>Geometry accompanies pixels so clipping can be diagnosed from the same rendering run.</summary>
internal static class SnapshotEvidence
{
    /// <summary>Renders the form to a PNG at <paramref name="path"/>, creating its directory.</summary>
    internal static Size SavePng(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return bitmap.Size;
    }

    internal static void WriteLayout(Form form, string imagePath)
    {
        object Describe(Control control) => new
        {
            type = control.GetType().Name,
            control.Name,
            bounds = new { control.Left, control.Top, control.Width, control.Height },
            client = new { control.ClientSize.Width, control.ClientSize.Height },
            minimum = new { control.MinimumSize.Width, control.MinimumSize.Height },
            margin = new { control.Margin.Left, control.Margin.Top, control.Margin.Right, control.Margin.Bottom },
            control.DeviceDpi,
            fontPoints = control.Font.SizeInPoints,
            control.Visible,
            columns = control is TableLayoutPanel table
                ? table.ColumnStyles.Cast<ColumnStyle>().Select(style => new { style.SizeType, style.Width }).ToArray() : null,
            children = control.Controls.Cast<Control>().Select(Describe).ToArray()
        };
        File.WriteAllText(imagePath + ".layout.json", JsonSerializer.Serialize(Describe(form), new JsonSerializerOptions { WriteIndented = true }));
    }
}
