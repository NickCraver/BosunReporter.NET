﻿using System;
using System.Collections.Generic;
using System.Threading;

namespace BosunReporter
{
    public class BosunCounter : BosunMetric
    {
        public long Value = 0;

        public override string MetricType
        {
            get { return "counter"; }
        }

        protected override IEnumerable<string> GetSerializedMetrics(string unixTimestamp)
        {
            yield return ToJson("", Value.ToString("D"), unixTimestamp);
        }

        public BosunCounter()
        {
        }

        public void Increment(long amount = 1)
        {
            if (!IsAttached)
            {
                var ex = new InvalidOperationException("Attempting to record on a gauge which is not attached to a BosunReporter object.");
                try
                {
                    ex.Data["Metric"] = Name;
                    ex.Data["Tags"] = SerializedTags;
                }
                finally
                {
                    throw ex;
                }
            }

            Interlocked.Add(ref Value, amount);
        }
    }
}
