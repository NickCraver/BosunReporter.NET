# Metric Types

## Counters

Counters are for _counting_ things. The most common use case is to increment a counter each time an event occurs. Bosun/OpenTSDB normalizes this data and is able to show you a rate (events per second) in the graphing interface.

### Counter

This is the basic counter type. It uses a `long` value and calls `Interlocked.Add()` internally to incrementing the value.

```csharp
var counter = collector.CreateMetric<Counter>("my_counter", "units", "description");

// increment by 1
counter.Increment();

// increment by more than 1
counter.Increment(23);
```

### SnapshotCounter

A snapshot counter is useful when you only care about updating the counter once per-reporting interval. The constructor takes a callback with the signature `Func<long?>` which will be called once per reporting interval. If the callback returns `null`, then no value is reported for the current interval.

```csharp
var count = 0;
collector.CreateMetric("name", "unit", "desc", new SnapshotCounter(() => count++));
```

### ExternalCounter

>  This feature requires you to be using [tsdbrelay](https://github.com/bosun-monitor/bosun/tree/master/cmd/tsdbrelay) as an intermediary between your app and Bosun. You'll need to run tsdbrelay with `-redis=REDIS_SERVER_NAME` and setup an [scollector](https://github.com/bosun-monitor/bosun/tree/master/cmd/scollector) instance to scrape it with:
>
> ```
> [[RedisCounters]] 
> Server = "localhost:6379" 
> Database = 2
> ```

External counters are intended to solve the problem of counting low-volume events.

The nature of a low-volume counter is that its per-second rate is going to be zero most of the time. For example:

![](https://i.stack.imgur.com/qD8Ki.png)

If you could simply see the start and end values for a given time interval, you would have a better sense of how frequent the events are. But, unfortunately, a normal Bosun counter resets every time the application restarts, so you end up with a graph that might look something like this when viewed as a gauge:

![](https://i.stack.imgur.com/wwGrO.png)

To solve this problem, external counters are persistent (the value doesn't reset every time the app restarts). Tsdbrelay stores the value of the counter in Redis, and BosunReporter sends it increments when an event happens. Tsdbrelay then periodically reports the metric to Bosun.

This means that when you graph the metric as a gauge, it will always be going up, and you can easily see start and end values for any time interval.

Remember, __these are for LOW VOLUME__ events. If you expect more than one event per minute across all instances of your app, you should use a normal counter.

You can enable/disable sending external counter increments using `BosunOptions.EnableExternalCounters` during initialization, or by changing `MetricsCollector.EnableExternalCounters` at runtime.

#### Usage

The usage of `ExternalCounter` is exactly the same as `Counter` except that you can only increment by 1.

```csharp
var counter = collector.CreateMetric<ExternalCounter>("ext_counter", "units", "description");
counter.Increment();
```

You can also inherit from `ExternalCounter` in order to add tags (like any other metric type).

>  tsdbrelay will automatically add the "host" tag. This means that metrics which inherit from ExternalCounter are not required to have any tags. ExternalCounter excludes the "host" tag by default for the same reason.

## Gauges

Gauges describe a measurement at a point in time. A good example would be measuring how much RAM is being consumed by a process. BosunReporter.NET provides several different types of gauges in order to support different programmatic use cases, but Bosun itself does not differentiate between these types. 

### SnapshotGauge

These are great for metrics where you want to record snapshots of a value, like CPU or memory usage. Pretend we have a method called `GetMemoryUsage` which returns a double. Now, let's write a snapshot gauge which calls that automatically at every metrics reporting interval.

```csharp
collector.CreateMetric("memory_usage", units, desc, new SnapshotGauge(() => GetMemoryUsage()));
```

That's it. There's no reason to even assign the gauge to a variable.

> __Why the lambda instead of just passing `GetMemoryUsage` to the constructor?__ In this contrived example, I said `GetMemoryUsage` returns a double. The SnapshotGauge constructor actually accepts a `Func<double?>`. It calls this function right before metrics are about to be flushed. If it returns a double value, the gauge reports that value. If it returns null, then the gauge does not report anything. This way, you have the flexibility of only reporting on something when there is sensible data to report.

### EventGauge

These are ideal for low-volume event-based data where it's practical to send all of the data points to Bosun. If you have a measurable event which occurs once every few seconds, then, instead of aggregating, you may want to use an event gauge. Every time you call `.Record()` on an event gauge, the metric will be serialized and queued. The queued metrics will be sent to Bosun on the normal reporting interval, like all other metrics.

```csharp
var myEvent = collector.CreateMetric<EventGauge>("my_event", units, desc);
someObject.OnSomeEvent += (sender, e) => myEvent.Record(someObject.Value);
```

### AggregateGauge

These are useful for event-based gauges where the volume of data points makes it undesirable or impractical to send them all to Bosun. For example, imagine you want to capture performance timings from five individual parts of your web request pipeline, and then report those numbers to Bosun. You might not want the number of metrics you send to Bosun to be 5x the number of your web requests, so the solution is to send aggregates.

Aggregate gauges come with six aggregators to choose from. You must use at least one for each gauge, but you can use as many as you'd like. BosunReporter.NET automatically expands the gauge into multiple metrics when sending to Bosun by appending suffixes to the metric name based on the aggregators in use.

| Name       | Default Suffix | Description                              |
| ---------- | -------------- | ---------------------------------------- |
| Average    | `_avg`         | The arithmetic mean.                     |
| Median     | `_median`      | 50th percentile.                         |
| Percentile | `_%%`          | Allows you to specify an arbitrary percentile (i.e. `0.95` for the 95th percentile). The default suffix is the integer representation of the percentage (i.e. `_95`). |
| Max        | `_max`         | The highest recorded value.              |
| Min        | `_min`         | The lowest recorded value.               |
| Last       | (no suffix)    | The last recorded value before the reporting/snapshot interval. |
| Count      | `_count`       | The number of events recorded during the reporting interval. |

All aggregators are reset at each reporting/snapshot interval. If no data points have been recorded since the last reporting interval, then only the `Count` aggregator (if present) will be sent to Bosun.

> By default, the minimum number of events which must be recorded before the AggregateGauge will report anything is one event per reporting interval. You can change this default by assigning your own `Func<int>` to the static `AggregateGauge.GetDefaultMinimumEvents` property. Or, you can override the `AggregateGauge.MinimumEvents` property on classes which inherit from AggregateGauge. This squelch feature does not apply to `Count` aggregators, which always report, regardless of how many events were recorded.

Let's create a simple route-timing metric which has a `route` tag, and reports on the median, 95th percentile, and max values. First, create a class which defines this gauge type.

```csharp
[GaugeAggregator(AggregateMode.Max)]
[GaugeAggregator(AggregateMode.Median)]
[GaugeAggregator(AggregateMode.Percentile, 0.95)]
public class RouteTimingGauge : AggregateGauge
{
	[BosunTag] public readonly string Route;

	public TestAggregateGauge(string route)
	{
		Route = route
	}
}
```

Then, instantiate the gauge for our route, and record timings to it.

```csharp
var testRouteTiming = collector.CreateMetric(
                                          "route_tr",
                                          units,
                                          desc,
                                          new RouteTimingGauge("Test/Route"));

testRouteTiming.Record(requestDuration);
```

If median or percentile aggregators are used, then _all_ values passed to the `Record()` method are stored in a `List<double>` until the next reporting interval, and must be sorted at that time in order to calculate the aggregate values. If you're concerned about this performance overhead, run some benchmarks on sorting a `List<double>` where the count is the number of data points you expect in-between metric reporting intervals. When there are multiple gauge metrics, the sorting is performed in parallel.

> Aggregate gauges use locks to achieve thread-safety currently. This is an implementation detail which may change if there is concurrency pattern which is shown to improve performance in highly parallel environments. Using a spin-wait pattern was also tried, but didn't yield any detectable improvement in testing.

### SamplingGauge

A sampling gauge simply reports the last recorded value at every reporting interval. They are similar to an aggregate gauge which only uses the "Last" aggregator. However, there are two differences:

1. In a sampling gauge, if no data has been recorded in the current snapshot/reporting interval, then the value from the previous interval is used. Whereas, an aggregate gauge won't report anything if no data was recorded during the interval.
2. The sampling gauge does not use locks to achieve thread safety, so it should perform slightly better than the "Last" aggregator, especially in highly concurrent environments.

If the last recorded value is `Double.NaN`, then nothing will be reported to Bosun.

```csharp
var sampler = collector.CreateMetric<SamplingGauge>("my_sampler", units, desc);
sampler.Record(1.2);
```

## Create Your Own

The built-in metric types described above should work for most use cases. However, you can also write your own by inheriting from the abstract `BosunMetric` class and writing an implementation for the `MetricType` property, and the `Serialize` method.

Both of the built-in counter types use a `long` as their value type. Here's how we might implement a floating point counter:

```csharp
public class DoubleCounter : BosunMetric
{
	// determines whether the metric is treated as a counter or gauge
	public override string MetricType { get { return "counter"; } }
	
	private object _lock = new object();
	public double Value { get; private set; }
	
	public void Increment(double amount = 1.0)
	{
		// Interlocked doesn't have an Increment() for doubles, so we have to use another
		// concurrency strategy. You should always keep thread-safety in mind when designing
		// your own metrics.
		lock (_lock) 
		{
			Value += amount;
		}
	}
	
	// this method is called by the collector when it's time to post to the Bosun API
	protected override void Serialize(MetricWriter writer, DateTime now)
	{
		// WriteValue is a protected method on BosunMetric
		WriteValue(writer, Value, now);
	}
}
```

### Multiple Suffixes

Most metrics don't need multiple suffixes; however, it's supported in the case that a single instance of a metric class actually needs to serialize as multiple metrics. The primary use case is `AggregateGauge` which serializes into multiple aggregates (e.g. `metric_avg`, `metric_max`, etc.).

The default is to have a single empty string suffix, but if your custom metric type needs to support multiple suffixes, then you'll need to override `GetImmutableSuffixesArray()`:

```csharp
protected override string[] GetImmutableSuffixesArray()
{
    // return array of suffixes
}
```

As the name implies, the list of suffixes must be immutable for the lifetime of the metric.

In order to serialize the value associated with each suffix, `WriteValue()` takes an optional fourth parameter which is the suffix index (defaults to zero). This value is an index into the array returned by `GetImmutableSuffixesArray()`.

If you'd like to use a different metadata description per suffix, you can also override `string GetDescription(int suffixIndex)`.

### Examples

For more examples, simply look at how the built-in metric types are implemented. See [/BosunReporter/Metrics](https://github.com/bretcope/BosunReporter.NET/tree/master/BosunReporter/Metrics)
