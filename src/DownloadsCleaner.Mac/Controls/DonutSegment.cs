namespace DownloadsCleaner.Controls;

/// <summary>Доля категории в кольцевой диаграмме (цвет - в hex, чтобы не зависеть от UI).</summary>
public sealed record DonutSegment(double Value, string ColorHex);
