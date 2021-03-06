// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.AspNetCore.Mvc.FunctionalTests
{
    public class DefaultValuesTest : IClassFixture<MvcTestFixture<BasicWebSite.Startup>>
    {
        public DefaultValuesTest(MvcTestFixture<BasicWebSite.Startup> fixture)
        {
            Client = fixture.Client;
        }

        public HttpClient Client { get; }

        [Fact]
        public async Task Controller_WithDefaultValueAttribut_ReturnsDefault()
        {
            // Arrange
            var expected = "hello";
            var url = "http://localhost/DefaultValues/EchoValue_DefaultValueAttribute";

            // Act
            var response = await Client.GetStringAsync(url);

            // Assert
            Assert.Equal(expected, response);
        }

        [Fact]
        public async Task Controller_WithDefaultValueAttribute_ReturnsModelBoundValues()
        {
            // Arrange
            var expected = "cool";
            var url = "http://localhost/DefaultValues/EchoValue_DefaultValueAttribute?input=cool";

            // Act
            var response = await Client.GetStringAsync(url);

            // Assert
            Assert.Equal(expected, response);
        }

        [Fact]
        public async Task Controller_WithDefaultParameterValue_ReturnsDefault()
        {
            // Arrange
            var expected = "world";
            var url = "http://localhost/DefaultValues/EchoValue_DefaultParameterValue";

            // Act
            var response = await Client.GetStringAsync(url);

            // Assert
            Assert.Equal(expected, response);
        }

        [Fact]
        public async Task Controller_WithDefaultParameterValue_ReturnsModelBoundValues()
        {
            // Arrange
            var expected = "cool";
            var url = "http://localhost/DefaultValues/EchoValue_DefaultParameterValue?input=cool";

            // Act
            var response = await Client.GetStringAsync(url);

            // Assert
            Assert.Equal(expected, response);
        }
    }
}
