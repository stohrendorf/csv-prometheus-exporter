using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;

namespace csv_prometheus_exporter.Prometheus
{
    public sealed class Histogram : LabeledMetric
    {
        public static readonly double[] DefaultBuckets =
            {.005, .01, .025, .05, .075, .1, .25, .5, .75, 1.0, 2.5, 5.0, 7.5, 10.0, double.PositiveInfinity};

        private readonly string _bucketName;

        private readonly double[] _buckets;
        private readonly string _countName;
        private readonly ulong[] _counts;
        private double _sum;
        private readonly string _sumName;
        private SpinLock _lock = new SpinLock();

        /// <summary>
        /// Placeholder value for the "le" label, to be replaced with the bucket border when exposed.
        /// </summary>
        private const string LePlaceholder = "$$$$$";

        public Histogram([NotNull] MetricBase metricBase, [NotNull] LabelDict labels, [NotNull] double[] buckets)
            : base(metricBase, labels)
        {
            Debug.Assert(metricBase.Type == MetricsType.Histogram);
            _buckets = buckets.OrderBy(_ => _).ToArray();
            if (_buckets.Length == 0)
                _buckets = DefaultBuckets;
            else if (!double.IsPositiveInfinity(_buckets.Last()))
                _buckets = _buckets.AsEnumerable().Concat(new[] {double.PositiveInfinity}).ToArray();

            if (_buckets.Length < 2)
                throw new ArgumentException("Must at least provide one bucket", nameof(buckets));

            _counts = Enumerable.Repeat(0UL, _buckets.Length).ToArray();
            _bucketName = ExtendBaseName(QualifiedName(LePlaceholder), "_bucket");
            var name = QualifiedName();
            _sumName = ExtendBaseName(name, "_sum");
            _countName = ExtendBaseName(name, "_count");
        }

        public override void ExposeTo(StreamWriter stream)
        {
            var counts = new ulong[_counts.Length];
            var buckets = new double[_buckets.Length];
            double sum;
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _counts.CopyTo(counts, 0);
                _buckets.CopyTo(buckets, 0);
                sum = _sum;
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }

            for (var i = 0; i < buckets.Length; ++i)
            {
                stream.WriteLine("{0} {1}", _bucketName.Replace(LePlaceholder, ToGoString(buckets[i])), counts[i]);
            }

            stream.WriteLine("{0} {1}", _countName, counts.Last());
            stream.WriteLine("{0} {1}", _sumName, sum);
        }

        public override void Add(double value)
        {
            var gotLock = false;
            try
            {
                _lock.Enter(ref gotLock);
                _sum += value;
                for (var i = 0; i < _buckets.Length; ++i)
                    if (value <= _buckets[i])
                    {
                        ++_counts[i];
                    }
            }
            finally
            {
                if (gotLock)
                    _lock.Exit();
            }
        }
    }
}
