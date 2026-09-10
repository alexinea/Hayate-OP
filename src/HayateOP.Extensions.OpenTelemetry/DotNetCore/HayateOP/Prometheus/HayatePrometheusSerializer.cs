using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DotNetCore.HayateOP.Prometheus;

/// <summary>
/// Prometheus text format (exposition format 0.0.4) serializer.
/// <para>
/// Zero external dependencies — does not pull in <c>prometheus-net</c> or the OTel Prometheus
/// exporter package; it only emits <c># HELP</c> / <c># TYPE</c> / <c>metric{label="v"} value</c>
/// per the Prometheus text protocol. Consumers can either expose it directly via an endpoint or
/// write the text to any scraping channel themselves.
/// </para>
/// </summary>
public static class HayatePrometheusSerializer
{
    /// <summary>The Prometheus text response Content-Type (protocol version 0.0.4).</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The pool-name label key (matches the OTel bridge's <c>pool.name</c> semantics, following Prometheus naming conventions).</summary>
    public const string PoolLabel = "pool";

    /// <summary>
    /// Escapes a label value: backslash, double quote, and newline are escaped per the Prometheus text protocol.
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
    /// Formats a numeric value (invariant culture): integers have no decimal point and floats use
    /// round-trip precision; NaN / ±Inf are emitted as the protocol literals.
    /// </summary>
    public static string FormatValue(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Writes a gauge metric family (with HELP / TYPE headers and one line per pool sample).</summary>
    public static void WriteGauge(StringBuilder builder, string name, string help, IReadOnlyList<HayatePrometheusSample> samples)
        => WriteMetricFamily(builder, name, help, "gauge", samples);

    /// <summary>Writes a counter metric family (with HELP / TYPE headers and one line per pool sample).</summary>
    public static void WriteCounter(StringBuilder builder, string name, string help, IReadOnlyList<HayatePrometheusSample> samples)
        => WriteMetricFamily(builder, name, help, "counter", samples);

    /// <summary>
    /// Writes a metric family: <c># HELP</c>, <c># TYPE</c>, then one line per sample.
    /// Emits nothing when the sample list is empty (avoids exposing an empty metric family).
    /// </summary>
    /// <param name="builder">The string builder to append to. Must not be null.</param>
    /// <param name="name">The metric name. Must not be null or whitespace.</param>
    /// <param name="help">The HELP text.</param>
    /// <param name="type">The metric type ("gauge", "counter", …).</param>
    /// <param name="samples">The samples to write. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="name"/> is null.</exception>
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
/// A single Prometheus sample (pool-name label + value).
/// </summary>
public readonly struct HayatePrometheusSample
{
    /// <summary>Creates a sample with a pool-name label.</summary>
    public HayatePrometheusSample(string labelValue, double value)
    {
        LabelValue = labelValue;
        Value = value;
    }

    /// <summary>The pool-name label value (<c>null</c> means a global, unlabeled sample).</summary>
    public string LabelValue { get; }

    /// <summary>The sample value.</summary>
    public double Value { get; }
}
