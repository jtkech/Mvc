// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.AspNetCore.Mvc.Razor
{
    public class RazorViewEngineOptionsTest
    {
        [Fact]
        public void AreaViewLocationFormats_ContainsExpectedLocations()
        {
            // Arrange
            var services = new ServiceCollection().AddOptions();
            var areaViewLocations = new string[]
            {
                "/Areas/{2}/Views/{1}/{0}.cshtml",
                "/Areas/{2}/Views/Shared/{0}.cshtml",
                "/Views/Shared/{0}.cshtml"
            };

            // Act
            var builder = new MvcBuilder(services, new ApplicationPartManager());
            builder.AddRazorOptions(options =>
            {
                options.AreaViewLocationFormats.Clear();

                foreach (var location in areaViewLocations)
                {
                    options.AreaViewLocationFormats.Add(location);
                }
            });
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var accessor = serviceProvider.GetRequiredService<IOptions<RazorViewEngineOptions>>();
            Assert.Equal(areaViewLocations, accessor.Value.AreaViewLocationFormats.ToArray());
        }

        [Fact]
        public void ViewLocationFormats_ContainsExpectedLocations()
        {
            // Arrange
            var services = new ServiceCollection().AddOptions();
            var viewLocations = new string[]
            {
                "/Views/{1}/{0}.cshtml",
                "/Views/Shared/{0}.cshtml"
            };

            // Act
            var builder = new MvcBuilder(services, new ApplicationPartManager());
            builder.AddRazorOptions(options =>
            {
                options.ViewLocationFormats.Clear();

                foreach (var location in viewLocations)
                {
                    options.ViewLocationFormats.Add(location);
                }
            });
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var accessor = serviceProvider.GetRequiredService<IOptions<RazorViewEngineOptions>>();
            Assert.Equal(viewLocations, accessor.Value.ViewLocationFormats.ToArray());
        }

        [Fact]
        public void AddRazorOptions_ConfiguresOptionsAsExpected()
        {
            // Arrange
            var services = new ServiceCollection().AddOptions();
            var fileProvider = new TestFileProvider();

            // Act
            var builder = new MvcBuilder(services, new ApplicationPartManager());
            builder.AddRazorOptions(options =>
            {
                options.FileProviders.Add(fileProvider);
            });
            var serviceProvider = services.BuildServiceProvider();

            // Assert
            var accessor = serviceProvider.GetRequiredService<IOptions<RazorViewEngineOptions>>();
            Assert.Same(fileProvider, accessor.Value.FileProviders[0]);
        }
    }
}