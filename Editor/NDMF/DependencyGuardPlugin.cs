using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(MofTools.Editor.DependencyGuardPlugin))]

namespace MofTools.Editor
{
    public sealed class DependencyGuardPlugin : Plugin<DependencyGuardPlugin>
    {
        public override string QualifiedName => "dev.moftools.dependencies";
        public override string DisplayName => "Mof Tools";
        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                .BeforePlugin("nadena.dev.modular-avatar")
                .BeforePlugin("dev.moftools.mesh-join")
                .Run("Check required assets", _ => ToolDependencies.RequireReady());
        }
    }
}
