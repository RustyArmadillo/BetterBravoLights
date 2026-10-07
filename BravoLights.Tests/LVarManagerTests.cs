using System;
using BravoLights.Ast;
using BravoLights.Common;
using BravoLights.Connections;
using Moq;
using Xunit;

namespace BravoLights.Tests
{
    public class LVarManagerTests
    {
        [Fact]
        public void SubscribesToNamedLVarAndPublishesReceivedValue()
        {
            var manager = new LVarManager();
            var channel = new Mock<ILVarChannel>(MockBehavior.Strict);
            channel.SetupGet(c => c.SimState).Returns(SimState.SimRunning);
            channel.Setup(c => c.Subscribe("Var2"));
            channel.Setup(c => c.Unsubscribe("Var2"));
            manager.SetLVarChannel(channel.Object);

            object received = null;
            EventHandler<ValueChangedEventArgs> handler = (sender, args) => received = args.NewValue;
            var expression = new LvarExpression { LVarName = "Var2" };

            manager.AddListener(expression, handler);
            manager.UpdateLVarValue("Var2", 42);

            Assert.Equal(42d, received);

            manager.RemoveListener(expression, handler);
            channel.VerifyAll();
        }
    }
}
