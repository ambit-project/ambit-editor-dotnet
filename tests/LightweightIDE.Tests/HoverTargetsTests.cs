using LightweightIDE.Language.Navigation;
using LightweightIDE.Language.RoslynWorkspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightweightIDE.Tests;

public class HoverTargetsTests
{
    const string Code = """
        using System.Collections.Generic;
        namespace Shop;
        public interface IOrderRepository { Order Find(int id); }
        public abstract class EntityBase { }
        public sealed class Order : EntityBase, System.IComparable<Order> { public int CompareTo(Order? other) => 0; }
        public sealed class Customer { }
        public class Checkout
        {
            readonly IOrderRepository repository = null!;
            public List<Order> Pay(Customer customer, int id) => [repository.Find(id)];
        }
        """;

    static async Task<HoverTargets?> At(string word, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++) index = Code.IndexOf(word, index + 1, StringComparison.Ordinal);
        using var workspace = new WorkspaceService(NullLogger<WorkspaceService>.Instance, new LanguageOptions());
        return await new NavigationService(workspace, new LanguageOptions()).GetHoverTargetsAsync("/tmp/hover/A.cs", Code, index, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Field_LinksItsInterface_WithImplementations()
    {
        var targets = await At("repository.Find");
        Assert.Equal(("repository", "field"), (targets!.Name, targets.Kind));
        Assert.NotNull(targets.Definition);
        Assert.Equal([("interface", "IOrderRepository")], targets.Links.Select(l => (l.Kind, l.Name)));
        Assert.False(targets.CanHaveImplementations);

        var type = await At("IOrderRepository repository");
        Assert.True(type!.CanHaveImplementations); // an interface
    }

    [Fact]
    public async Task Method_LinksReturnParameterAndOwnerTypes_UnwrappingGenerics()
    {
        var targets = await At("Pay");
        Assert.Equal(["Order", "Customer", "Checkout"], targets!.Links.Select(l => l.Name)); // List<Order> → Order; int has no source
    }

    [Fact]
    public async Task Class_LinksBaseClass_AndSourceInterfacesOnly()
    {
        var targets = await At("Order : EntityBase");
        Assert.Equal([("class", "EntityBase")], targets!.Links.Select(l => (l.Kind, l.Name))); // IComparable<T> is metadata
        Assert.False(targets.CanHaveImplementations); // sealed
    }
}
