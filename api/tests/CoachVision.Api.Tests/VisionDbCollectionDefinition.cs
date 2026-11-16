using CoachVision.TestSupport;

namespace CoachVision.Api.Tests;

/// <summary>Redeclared per assembly: see the remarks on <see cref="VisionDbCollection"/>.</summary>
[CollectionDefinition(VisionDbCollection.Name)]
public sealed class VisionDbCollectionDefinition : ICollectionFixture<VisionPostgresFixture>;
