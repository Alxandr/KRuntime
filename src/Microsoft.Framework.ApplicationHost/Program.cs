// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Framework.ApplicationHost.Impl.Syntax;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Common;
using Microsoft.Framework.Runtime.Common.CommandLine;

namespace Microsoft.Framework.ApplicationHost
{
    public class Program
    {
        private readonly IAssemblyLoaderContainer _container;
        private readonly IApplicationEnvironment _environment;
        private readonly IServiceProvider _serviceProvider;

        public Program(IAssemblyLoaderContainer container, IApplicationEnvironment environment, IServiceProvider serviceProvider)
        {
            _container = container;
            _environment = environment;
            _serviceProvider = serviceProvider;
        }

        public async Task<int> Main(string[] args)
        {
            var parseResult = await ParseArgs(args);
            if (parseResult.IsShowingInformation)
            {
                return 0;
            }

            var host = new DefaultHost(parseResult.Options, _serviceProvider);

            if (host.Project == null)
            {
                return -1;
            }

            var lookupCommand = string.IsNullOrEmpty(
                parseResult.Options.ApplicationName) ? "run" : parseResult.Options.ApplicationName;

            string replacementCommand;
            if (host.Project.Commands.TryGetValue(lookupCommand, out replacementCommand))
            {
                var replacementArgs = CommandGrammar.Process(
                    replacementCommand,
                    GetVariable).ToArray();
                parseResult.Options.ApplicationName = replacementArgs.First();
                parseResult.ProgramArgs = replacementArgs.Skip(1).Concat(parseResult.ProgramArgs).ToArray();
            }

            if (string.IsNullOrEmpty(parseResult.Options.ApplicationName) ||
                string.Equals(parseResult.Options.ApplicationName, "run", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(host.Project.Name))
                {
                    parseResult.Options.ApplicationName = Path.GetFileName(parseResult.Options.ApplicationBaseDirectory);
                }
                else
                {
                    parseResult.Options.ApplicationName = host.Project.Name;
                }
            }

            using(var disposable = host.AddLoaders(_container))
            {
                return await ExecuteMain(host, parseResult.Options.ApplicationName, parseResult.ProgramArgs);
            }
        }

        private string GetVariable(string key)
        {
            if (string.Equals(key, "env:ApplicationBasePath", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.ApplicationBasePath;
            }
            if (string.Equals(key, "env:ApplicationName", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.ApplicationName;
            }
            if (string.Equals(key, "env:Version", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.Version;
            }
            if (string.Equals(key, "env:TargetFramework", StringComparison.OrdinalIgnoreCase))
            {
                return _environment.TargetFramework.Identifier;
            }
            return Environment.GetEnvironmentVariable(key);
        }


        private async Task<ParseResult> ParseArgs(string[] args)
        {
            DefaultHostOptions defaultHostOptions;
            string[] outArgs;

            var app = new CommandLineApplication(throwOnUnexpectedArg: false);
            app.Name = "k";
            var optionWatch = app.Option("--watch", "Watch file changes", CommandOptionType.NoValue);
            var optionPackages = app.Option("--packages <PACKAGE_DIR>", "Directory containing packages",
                CommandOptionType.SingleValue);
            var optionConfiguration = app.Option("--configuration <CONFIGURATION>", "The configuration to run under", CommandOptionType.SingleValue);

            var runCmdExecuted = false;
            app.HelpOption("-?|-h|--help");
            app.VersionOption("--version", GetVersion());
            var runCmd = app.Command("run", c =>
            {
                // We don't actually execute "run" command here
                // We are adding this command for the purpose of displaying correct help information
                c.Description = "Run application";
                c.OnExecute(() =>
                {
                    runCmdExecuted = true;
                    return 0;
                });
            },
            addHelpCommand: false,
            throwOnUnexpectedArg: false);
            await app.Execute(args);

            if (!(app.IsShowingInformation || app.RemainingArguments.Any() || runCmdExecuted))
            {
                app.ShowHelp(commandName: null);
            }

            defaultHostOptions = new DefaultHostOptions();
            defaultHostOptions.WatchFiles = optionWatch.HasValue();
            defaultHostOptions.PackageDirectory = optionPackages.Value();

            defaultHostOptions.TargetFramework = _environment.TargetFramework;
            defaultHostOptions.Configuration = optionConfiguration.Value() ?? _environment.Configuration ?? "debug";
            defaultHostOptions.ApplicationBaseDirectory = _environment.ApplicationBasePath;

            var remainingArgs = new List<string>();
            if (runCmdExecuted)
            {
                // Later logic will execute "run" command
                // So we put this argment back after it was consumed by parser
                remainingArgs.Add("run");
                remainingArgs.AddRange(runCmd.RemainingArguments);
            }
            else
            {
                remainingArgs.AddRange(app.RemainingArguments);
            }

            if (remainingArgs.Any())
            {
                defaultHostOptions.ApplicationName = remainingArgs[0];
                outArgs = remainingArgs.Skip(1).ToArray();
            }
            else
            {
                outArgs = remainingArgs.ToArray();
            }

            return new ParseResult
            {
                IsShowingInformation = app.IsShowingInformation,
                Options = defaultHostOptions,
                ProgramArgs = outArgs
            };
        }

        private Task<int> ExecuteMain(DefaultHost host, string applicationName, string[] args)
        {
            Assembly assembly = null;

            try
            {
                assembly = host.GetEntryPoint(applicationName);
            }
            catch (FileLoadException ex)
            {
                // FileName is always turned into an assembly name
                if (new AssemblyName(ex.FileName).Name == applicationName)
                {
                    ThrowEntryPointNotfoundException(
                        host,
                        applicationName,
                        ex.InnerException);
                }
                else
                {
                    throw;
                }
            }
            catch (FileNotFoundException ex)
            {
                if (ex.FileName == applicationName)
                {
                    ThrowEntryPointNotfoundException(
                        host,
                        applicationName,
                        ex.InnerException);
                }
                else
                {
                    throw;
                }
            }

            if (assembly == null)
            {
                return Task.FromResult(-1);
            }

            return EntryPointExecutor.Execute(assembly, args, host.ServiceProvider);
        }

        private static void ThrowEntryPointNotfoundException(
            DefaultHost host,
            string applicationName,
            Exception innerException)
        {

            var compilationException = innerException as CompilationException;

            if (compilationException != null)
            {
                throw new InvalidOperationException(
                    string.Join(Environment.NewLine, compilationException.Errors));
            }

#if K10
            // HACK: Don't show inner exceptions for non compilation errors.
            // There's a bug in the CoreCLR loader, where it throws another
            // invalid operation exception for any load failure with a bizzare
            // message.
            innerException = null;
#endif

            if (host.Project.Commands.Any())
            {
                // Throw a nicer exception message if the command
                // can't be found
                throw new InvalidOperationException(
                    string.Format("Unable to load application or execute command '{0}'. Available commands: {1}.",
                    applicationName,
                    string.Join(", ", host.Project.Commands.Keys)), innerException);
            }

            throw new InvalidOperationException(
                    string.Format("Unable to load application or execute command '{0}'.",
                    applicationName), innerException);
        }

        private static string GetVersion()
        {
            var assembly = typeof(Program).GetTypeInfo().Assembly;
            var assemblyInformationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return assemblyInformationalVersionAttribute.InformationalVersion;
        }

        private struct ParseResult
        {
            public bool IsShowingInformation { get; set; }
            public DefaultHostOptions Options { get; set; }
            public string[] ProgramArgs { get; set; }
        }
    }
}
