using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DotNetCore.HayateOP.Prometheus;

/// <summary>
/// M22：Prometheus 文本格式（exposition format 0.0.4）序列化器。
/// <para>
/// 零外部依赖——不引入 <c>prometheus-net</c> 或 OTel Prometheus exporter 包，
/// 仅按 Prometheus 文本协议输出 <c># HELP</c> / <c># TYPE</c> / <c>metric{label="v"} value</c>。
/// 使用方既可通过端点直接暴露，也可自行把文本写入任意采集通道。
/// </para>
/// </summary>
public static class HayatePrometheusSerializer
{
    /// <summary>Prometheus 文本响应 Content-Type（协议版本 0.0.4）。</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>池名标签键（与 OTel 桥接的 <c>pool.name</c> 语义一致，命名遵循 Prometheus 惯例）。</summary>
    public const string PoolLabel = "pool";

    /// <summary>
    /// 转义标签值：反斜杠、双引号、换行按 Prometheus 文本协议要求转义。
    /// </summary>
    public static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 数值格式化（不变文化）：整数不带小数点，浮点用往返精度；NaN / ±Inf 按协议字面量输出。
    /// </summary>
    public static string FormatValue(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 写入一个 gauge 指标族（含 HELP / TYPE 头与逐池样本行）。
    /// </summary>
    public static void WriteGauge(StringBuilder builder, string name, string help, IReadOnlyList<HayatePrometheusSample> samples)
        => WriteMetricFamily(builder, name, help, "gauge", samples);

    /// <summary>
    /// 写入一个 counter 指标族（含 HELP / TYPE 头与逐池样本行）。
    /// </summary>
    public static void WriteCounter(StringBuilder builder, string name, string help, IReadOnlyList<HayatePrometheusSample> samples)
        => WriteMetricFamily(builder, name, help, "counter", samples);

    /// <summary>
    /// 写入指标族：<c># HELP</c>、<c># TYPE</c>，随后每个样本一行。
    /// 样本为空时不输出任何内容（避免暴露空指标族）。
    /// </summary>
    public static void WriteMetricFamily(
        StringBuilder builder,
        string name,
        string help,
        string type,
        IReadOnlyList<HayatePrometheusSample> samples)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
        if (samples is null || samples.Count == 0) return;

        builder.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        builder.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.LabelValue is null)
            {
                builder.Append(name).Append(' ').Append(FormatValue(sample.Value)).Append('\n');
            }
            else
            {
                builder.Append(name)
                       .Append('{').Append(PoolLabel).Append("=\"").Append(EscapeLabelValue(sample.LabelValue)).Append("\"} ")
                       .Append(FormatValue(sample.Value)).Append('\n');
            }
        }
    }
}

/// <summary>
/// M22：单条 Prometheus 样本（池名标签 + 数值）。
/// </summary>
public readonly struct HayatePrometheusSample
{
    /// <summary>创建带池名标签的样本。</summary>
    public HayatePrometheusSample(string labelValue, double value)
    {
        LabelValue = labelValue;
        Value = value;
    }

    /// <summary>池名标签值（<c>null</c> 表示无标签的全局样本）。</summary>
    public string LabelValue { get; }

    /// <summary>样本数值。</summary>
    public double Value { get; }
}
