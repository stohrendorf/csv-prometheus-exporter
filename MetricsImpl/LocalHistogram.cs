using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.MetricsImpl
{
    public sealed class LocalHistogram : LocalMetrics
    {
        public static readonly double[] DefaultBuckets =
            {.005, .01, .025, .05, .075, .1, .25, .5, .75, 1.0, 2.5, 5.0, 7.5, 10.0, double.PositiveInfinity};

        private readonly double[] _buckets;
        private readonly ulong[] _counts;
        private ulong _observations;
        private double _sum;
        private readonly string _bucketName;
        private readonly string _sumName;
        private readonly string _countName;

        public LocalHistogram([NotNull] MetricsMeta meta, [NotNull] Dictionary<string, string> labels,
            [NotNull] double[] buckets) : base(meta, labels)
        {
            Debug.Assert(meta.Type == Type.Histogram);
            _buckets = buckets.OrderBy(_ => _).ToArray();
            if (_buckets.Length == 0)
                _buckets = DefaultBuckets;
            else if (!double.IsPositiveInfinity(_buckets.Last()))
                _buckets = _buckets.AsEnumerable().Concat(new[] {double.PositiveInfinity}).ToArray();

            if (_buckets.Length < 2)
                throw new ArgumentException("Must at least provide one bucket", nameof(buckets));

            _counts = Enumerable.Range(0, _buckets.Length).Select(_ => 0UL).ToArray();
            _bucketName = QualifiedName(new Dictionary<string, string> {["le"] = "$$$$$"});
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        private LocalHistogram(LocalHistogram self) : base(self.Meta, self.Labels)
        {
            _buckets = self._buckets;
            _counts = (ulong[]) self._counts.Clone();
            _bucketName = self._bucketName;
            _sumName = self._sumName;
            _countName = self._countName;
        }

        public override void ExposeTo(StreamWriter stream)
        {
            for (int i = 0; i < _buckets.Length; ++i)
            {
                stream.WriteLine("{0} {1}", _bucketName.Replace("$$$$$", ToGoString(_buckets[i])), _counts[i]);
            }

            stream.WriteLine("{0} {1}", _countName, _observations);
            stream.WriteLine("{0} {1}", _sumName, _sum.ToString(CultureInfo.InvariantCulture));
        }

        public override void Add(double value)
        {
            ++_observations;
            _sum += value;
            for (int i = 0; i < _buckets.Length; ++i)
            {
                if (value <= _buckets[i])
                {
                    ++_counts[i];
                    break;
                }
            }
        }

        public override void MergeAll(LocalMetrics other)
        {
#if DEBUG
            if (!(other is LocalHistogram o))
                throw new ArgumentException("Incompatible type", nameof(other));
#else
            var o = (LocalHistogram) other;
#endif

            _observations += o._observations;
            _sum += o._sum;
            for (int i = 0; i < _counts.Length; ++i)
                _counts[i] += o._counts[i];
        }

        public override LocalMetrics Clone()
        {
            return new LocalHistogram(this);
        }
    }
}