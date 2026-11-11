// Browser tests share one Chromium and fixed fixture servers; run them one at a time for predictable resources.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
