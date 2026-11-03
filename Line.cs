namespace NetSpeedTray;

// Kind: 0 = upload, 1 = download, 2 = total
public readonly record struct Line(string Num, string Unit, int TextArgb, int ArrowArgb, int Kind)
{
    public string Text => Num + Unit;
}
