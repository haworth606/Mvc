﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
#if GENERATE_SQL_SCRIPTS
using System.Linq;
#endif
using System.Security.Cryptography.X509Certificates;
using BasicApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Serialization;
using Npgsql;

namespace BasicApi
{
    public class Startup
    {
        private bool _isSQLite;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<SecurityKey, TestSecurityKey>(); // For TokenController and TestSigningCredentials
            services.AddSingleton<SigningCredentials, TestSigningCredentials>(); // For ConfigureJwtBearerOptions
            services.ConfigureOptions<ConfigureJwtBearerOptions>();
            services.AddAuthentication().AddJwtBearer();

            var connectionString = Configuration["ConnectionString"];
            var databaseType = Configuration["Database"];
            if (string.IsNullOrEmpty(databaseType))
            {
                // Use SQLite when running outside a benchmark test or if benchmarks user specified "None".
                // ("None" is not passed to the web application.)
                databaseType = "SQLite";
            }
            else if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException("Connection string must be specified for {databaseType}.");
            }

            switch (databaseType.ToUpper())
            {
#if !NET461
                case "MYSQL":
                    services
                        .AddEntityFrameworkMySql()
                        .AddDbContextPool<BasicApiContext>(options => options.UseMySql(connectionString));
                    break;
#endif

                case "POSTGRESQL":
                    var settings = new NpgsqlConnectionStringBuilder(connectionString);
                    if (!settings.NoResetOnClose)
                    {
                        throw new ArgumentException("No Reset On Close=true must be specified for Npgsql.");
                    }
                    if (settings.Enlist)
                    {
                        throw new ArgumentException("Enlist=false must be specified for Npgsql.");
                    }

                    services
                        .AddEntityFrameworkNpgsql()
                        .AddDbContextPool<BasicApiContext>(options => options.UseNpgsql(connectionString));
                    break;

                case "SQLITE":
                    _isSQLite = true;
                    services
                        .AddEntityFrameworkSqlite()
                        .AddDbContextPool<BasicApiContext>(options => options.UseSqlite("Data Source=BasicApi.db"));
                    break;

                case "SQLSERVER":
                    services
                        .AddEntityFrameworkSqlServer()
                        .AddDbContextPool<BasicApiContext>(options => options.UseSqlServer(connectionString));
                    break;

                default:
                    throw new ArgumentException($"Application does not support database type {databaseType}.");
            }

            services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    "pet-store-reader",
                    builder => builder
                        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim("scope", "pet-store-reader"));

                options.AddPolicy(
                    "pet-store-writer",
                    builder => builder
                        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim("scope", "pet-store-writer"));
            });

            services
                .AddMvcCore()
                .AddAuthorization()
                .AddJsonFormatters(json => json.ContractResolver = new CamelCasePropertyNamesContractResolver())
                .AddDataAnnotations();
        }

        public void Configure(IApplicationBuilder app, IApplicationLifetime lifetime)
        {
            var services = app.ApplicationServices;
            CreateDatabaseTables(services);
            if (_isSQLite)
            {
                lifetime.ApplicationStopping.Register(() => DropDatabase(services));
            }
            else
            {
                lifetime.ApplicationStopping.Register(() => DropDatabaseTables(services));
            }

            app.Use(next => async context =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    throw;
                }
            });

            app.UseAuthentication();
            app.UseMvc();
        }

        private void CreateDatabaseTables(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
#if GENERATE_SQL_SCRIPTS
                    var migrator = dbContext.GetService<IMigrator>();
                    var script = migrator.GenerateScript(
                        fromMigration: Migration.InitialDatabase,
                        toMigration: dbContext.Database.GetMigrations().LastOrDefault());
                    Console.WriteLine("Create script:");
                    Console.WriteLine(script);
#endif

                    dbContext.Database.Migrate();
                }
            }
        }

        // Don't leave SQLite's .db file behind.
        public static void DropDatabase(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
#if GENERATE_SQL_SCRIPTS
                    var migrator = dbContext.GetService<IMigrator>();
                    var script = migrator.GenerateScript(
                        fromMigration: dbContext.Database.GetAppliedMigrations().LastOrDefault(),
                        toMigration: Migration.InitialDatabase);
                    Console.WriteLine("Delete script:");
                    Console.WriteLine(script);
#endif

                    dbContext.Database.EnsureDeleted();
                }
            }
        }

        private void DropDatabaseTables(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
                    var migrator = dbContext.GetService<IMigrator>();
#if GENERATE_SQL_SCRIPTS
                    var script = migrator.GenerateScript(
                        fromMigration: dbContext.Database.GetAppliedMigrations().LastOrDefault(),
                        toMigration: Migration.InitialDatabase);
                    Console.WriteLine("Delete script:");
                    Console.WriteLine(script);
#endif

                    migrator.Migrate(Migration.InitialDatabase);
                }
            }
        }

        public static void Main(string[] args)
        {
            var host = CreateWebHostBuilder(args)
                .Build();

            host.Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            return new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://+:5000")
                .UseConfiguration(configuration)
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>();
        }

        private class TestSecurityKey : X509SecurityKey
        {
            public TestSecurityKey(IHostingEnvironment hostingEnvironment)
                : base(new X509Certificate2(
                      fileName: Path.Combine(hostingEnvironment.ContentRootPath, "testCert.pfx"),
                      password: "DO_NOT_USE_THIS_CERT_IN_PRODUCTION"))
            {
            }
        }

        private class TestSigningCredentials : SigningCredentials
        {
            public TestSigningCredentials(SecurityKey securityKey)
                : base(securityKey, SecurityAlgorithms.RsaSha256Signature)
            {
            }
        }

        private class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
        {
            private readonly SecurityKey _securityKey;

            public ConfigureJwtBearerOptions(SecurityKey securityKey)
            {
                _securityKey = securityKey;
            }

            public void Configure(JwtBearerOptions options)
            {
                // No-op. Never called in this app.
            }

            public void Configure(string name, JwtBearerOptions options)
            {
                // Ignore the name. App only uses JwtBearerDefaults.AuthenticationScheme.
                if (options == null)
                {
                    throw new ArgumentNullException(nameof(options));
                }

                options.TokenValidationParameters.IssuerSigningKey = _securityKey;
                options.TokenValidationParameters.ValidAudience = "Myself";
                options.TokenValidationParameters.ValidIssuer = "BasicApi";
            }
        }
    }
}