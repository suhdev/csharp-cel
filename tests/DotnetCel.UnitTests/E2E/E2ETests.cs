using DotnetCel.Types;

namespace DotnetCel.UnitTests.E2E;

public class E2ETests
{
    [Fact]
    public void Test()
    {
        var env = CelEnv.NewBuilder()
            .Variable("user", CelTypes.Object("User"))
            .Variable("account", CelTypes.Object("Account"))
            .Build();

        var program = CelExpression.Compile(
            "account.address.city == 'London'",
            env);

        bool ok = (bool)program.Eval(new
        {
            user = new { Name = "alice", Age = 25 },
            account = new Account { Name = "alice", Age = 25, Address = new AddressDetails()
            {
                Street = "123 Main St",
                City = "London"
            }}
        })!;

        Assert.True(ok);
    }

    public class Account
    {
        public AddressDetails Address { get; set; } = new AddressDetails();
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    public class AddressDetails
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
    }
}
