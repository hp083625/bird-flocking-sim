// AssemblyInfo.cs — exposes Behaviors internals to:
//   1. Simulation (so FlockWorld.Tick can build per-flock kernel-settings snapshots
//      and dispatch the Slice 4 job graph through SteeringJobGraph without forcing
//      FlockKernelSettings / the per-job structs to be public).
//   2. Tests.EditMode (so M6-2 force-kernel tests can call ForceKernels.* directly).
//   3. Tests.PlayMode (so M6-4 allocation regression test can construct kernel
//      settings + dispatch the job graph in isolation if needed).

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bird_behiviour.Flocking.Simulation")]
[assembly: InternalsVisibleTo("Bird_behiviour.Flocking.Tests.EditMode")]
[assembly: InternalsVisibleTo("Bird_behiviour.Flocking.Tests.PlayMode")]
