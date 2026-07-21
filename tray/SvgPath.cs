using System.Drawing.Drawing2D;
using System.Globalization;

namespace OpenUsageTray;

// Minimal SVG path (`d` attribute) → GraphicsPath, supporting M/L/H/V/C/S/Q/T/Z (absolute + relative,
// implicit repeats). Enough for the single-path provider marks in web/icons/*.svg — a direct port of
// the app's SVGPath parser so the tray strip draws the same glyphs. No arcs (the marks don't use them).
static class SvgPath
{
    public static GraphicsPath? FromFile(string file, RectangleF target, float inset = 0.06f)
    {
        try
        {
            string svg = File.ReadAllText(file);
            string? d = ExtractD(svg);
            if (d == null) return null;
            var raw = Parse(d);
            var b = raw.GetBounds();
            if (b.Width <= 0 || b.Height <= 0) return null;
            var box = RectangleF.Inflate(target, -target.Width * inset, -target.Height * inset);
            float scale = Math.Min(box.Width / b.Width, box.Height / b.Height);
            float dx = box.Left + box.Width / 2 - (b.Left + b.Width / 2) * scale;
            float dy = box.Top + box.Height / 2 - (b.Top + b.Height / 2) * scale;
            var m = new Matrix();
            m.Translate(dx, dy);
            m.Scale(scale, scale);
            raw.Transform(m);
            return raw;
        }
        catch { return null; }
    }

    private static string? ExtractD(string svg)
    {
        var parts = new List<string>();
        int i = 0;
        while (true)
        {
            int s = svg.IndexOf("d=\"", i, StringComparison.Ordinal);
            if (s < 0) break;
            int e = svg.IndexOf('"', s + 3);
            if (e < 0) break;
            parts.Add(svg.Substring(s + 3, e - (s + 3)));
            i = e + 1;
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static GraphicsPath Parse(string d)
    {
        var path = new GraphicsPath(FillMode.Winding);
        var c = d.ToCharArray();
        int n = c.Length, i = 0;
        PointF cur = default, start = default, lastCtrl = default;
        char cmd = ' ';
        bool prevCubic = false, prevQuad = false;
        bool open = false;

        void Skip() { while (i < n && (c[i] == ' ' || c[i] == ',' || c[i] == '\n' || c[i] == '\t' || c[i] == '\r')) i++; }
        float? Num()
        {
            Skip();
            int st = i;
            if (i < n && (c[i] == '+' || c[i] == '-')) i++;
            bool dot = false;
            while (i < n)
            {
                char ch = c[i];
                if (char.IsDigit(ch)) i++;
                else if (ch == '.' && !dot) { dot = true; i++; }
                else if (ch == 'e' || ch == 'E') { i++; if (i < n && (c[i] == '+' || c[i] == '-')) i++; }
                else break;
            }
            if (i == st) return null;
            return float.TryParse(d.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        PointF? Pt(bool rel)
        {
            var x = Num(); var y = Num();
            if (x == null || y == null) return null;
            return rel ? new PointF(cur.X + x.Value, cur.Y + y.Value) : new PointF(x.Value, y.Value);
        }
        PointF Refl() => lastCtrl == default ? cur : new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y);
        void Line(PointF p) { path.AddLine(cur, p); cur = p; }

        while (i < n)
        {
            Skip();
            if (i >= n) break;
            if (char.IsLetter(c[i])) { cmd = c[i]; i++; }
            char k = cmd;
            bool cub = false, quad = false, fail = false;
            switch (k)
            {
                case 'M': case 'm':
                    { var p = Pt(k == 'm'); if (p == null) { fail = true; break; } path.StartFigure(); cur = p.Value; start = p.Value; open = true; cmd = k == 'm' ? 'l' : 'L'; }
                    break;
                case 'L': case 'l': { var p = Pt(k == 'l'); if (p == null) { fail = true; break; } Line(p.Value); } break;
                case 'H': case 'h': { var x = Num(); if (x == null) { fail = true; break; } Line(new PointF(k == 'h' ? cur.X + x.Value : x.Value, cur.Y)); } break;
                case 'V': case 'v': { var y = Num(); if (y == null) { fail = true; break; } Line(new PointF(cur.X, k == 'v' ? cur.Y + y.Value : y.Value)); } break;
                case 'C': case 'c':
                    { var a = Pt(k == 'c'); var b = Pt(k == 'c'); var e = Pt(k == 'c'); if (a == null || b == null || e == null) { fail = true; break; } path.AddBezier(cur, a.Value, b.Value, e.Value); cur = e.Value; lastCtrl = b.Value; cub = true; }
                    break;
                case 'S': case 's':
                    { var b = Pt(k == 's'); var e = Pt(k == 's'); if (b == null || e == null) { fail = true; break; } var a = prevCubic ? Refl() : cur; path.AddBezier(cur, a, b.Value, e.Value); cur = e.Value; lastCtrl = b.Value; cub = true; }
                    break;
                case 'Q': case 'q':
                    { var a = Pt(k == 'q'); var e = Pt(k == 'q'); if (a == null || e == null) { fail = true; break; } AddQuad(path, cur, a.Value, e.Value); cur = e.Value; lastCtrl = a.Value; quad = true; }
                    break;
                case 'T': case 't':
                    { var e = Pt(k == 't'); if (e == null) { fail = true; break; } var a = prevQuad ? Refl() : cur; AddQuad(path, cur, a, e.Value); cur = e.Value; lastCtrl = a; quad = true; }
                    break;
                case 'Z': case 'z': if (open) { path.CloseFigure(); open = false; } cur = start; break;
                default: fail = true; break;
            }
            if (fail) break;
            prevCubic = cub; prevQuad = quad;
        }
        return path;
    }

    private static void AddQuad(GraphicsPath path, PointF p0, PointF c1, PointF p2)
    {
        // quadratic → cubic control points
        var b1 = new PointF(p0.X + 2f / 3f * (c1.X - p0.X), p0.Y + 2f / 3f * (c1.Y - p0.Y));
        var b2 = new PointF(p2.X + 2f / 3f * (c1.X - p2.X), p2.Y + 2f / 3f * (c1.Y - p2.Y));
        path.AddBezier(p0, b1, b2, p2);
    }
}
