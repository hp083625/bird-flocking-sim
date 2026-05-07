// AssemblyInfo.cs — exposes Spatial internals (SpatialHashGrid backing storage,
// BuildGridJob's intermediate state) to the EditMode test assembly so the unit
// tests in Tests.EditMode can drive the cell-list grid directly without going
// through the public ISpatialIndex contract.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bird_behiviour.Flocking.Tests.EditMode")]
