﻿/*
 * Markdown Scanner
 * Copyright (c) Microsoft Corporation
 * All rights reserved. 
 * 
 * MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of 
 * this software and associated documentation files (the ""Software""), to deal in 
 * the Software without restriction, including without limitation the rights to use, 
 * copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
 * Software, and to permit persons to whom the Software is furnished to do so, 
 * subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all 
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, 
 * INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A 
 * PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT 
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION 
 * OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
 * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

namespace ApiDocs.ConsoleApp
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using ApiDocs.ConsoleApp.AppVeyor;
    using ApiDocs.ConsoleApp.Auth;
    using ApiDocs.Publishing.Html;
    using ApiDocs.Publishing.Swagger;
    using ApiDocs.Validation;
    using ApiDocs.Validation.Error;
    using ApiDocs.Validation.Http;
    using ApiDocs.Validation.Json;
    using ApiDocs.Validation.OData;
    using ApiDocs.Validation.Params;
    using ApiDocs.Validation.Writers;
    using CommandLine;
    using Newtonsoft.Json;
    

    class Program
    {
        private const int ExitCodeFailure = 1;
        private const int ExitCodeSuccess = 0;
        private const int ParallelTaskCount = 5;

        public static readonly BuildWorkerApi BuildWorker = new BuildWorkerApi();
        public static AppConfigFile CurrentConfiguration { get; private set; }

        static void Main(string[] args)
        {
            Logging.ProviderLogger(new ConsoleAppLogger());

            FancyConsole.WriteLine(ConsoleColor.Green, "APIDocs tool, version {0}", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
            FancyConsole.WriteLine();
            if (args.Length > 0)
                FancyConsole.WriteLine("Command line: " + args.ComponentsJoinedByString(" "));
            

            string verbName = null;
            BaseOptions verbOptions = null;

            var options = new CommandLineOptions();
            if (!Parser.Default.ParseArguments(args, options,
              (verb, subOptions) =>
              {
                  // if parsing succeeds the verb name and correct instance
                  // will be passed to onVerbCommand delegate (string,object)
                  verbName = verb;
                  verbOptions = (BaseOptions)subOptions;
              }))
            {
                Exit(failure: true);
            }

#if DEBUG
            if (verbOptions.AttachDebugger)
            {
                Debugger.Launch();
            }
#endif

            if (!string.IsNullOrEmpty(verbOptions.AppVeyorServiceUrl))
            {
                BuildWorker.UrlEndPoint = new Uri(verbOptions.AppVeyorServiceUrl);
            }

            var commandOptions = verbOptions as DocSetOptions;
            if (null != commandOptions)
            {
                FancyConsole.WriteVerboseOutput = commandOptions.EnableVerboseOutput;
            }

            FancyConsole.LogFileName = verbOptions.LogFile;

            var task = Task.Run(() => RunInvokedMethodAsync(options, verbName, verbOptions));
            task.Wait();
        }

        public static void LoadCurrentConfiguration(DocSetOptions options)
        {
            if (CurrentConfiguration != null)
                return;

            if (null != options)
            {
                var configurationFiles = DocSet.TryLoadConfigurationFiles<AppConfigFile>(options.DocumentationSetPath);
                CurrentConfiguration = configurationFiles.FirstOrDefault();
                if (null != CurrentConfiguration)
                    Console.WriteLine("Using configuration file: {0}", CurrentConfiguration.SourcePath);
            }
        }

        private static async Task RunInvokedMethodAsync(CommandLineOptions origCommandLineOpts, string invokedVerb, BaseOptions options)
        {
            string[] missingProps;
            if (!options.HasRequiredProperties(out missingProps))
            {
                var error = new ValidationError(ValidationErrorCode.MissingRequiredArguments, null, "Command line is missing required arguments: {0}", missingProps.ComponentsJoinedByString(", "));
                FancyConsole.WriteLine(origCommandLineOpts.GetUsage(invokedVerb));
                await WriteOutErrorsAndFinishTestAsync(new ValidationError[] { error }, options.SilenceWarnings);
                Exit(failure: true);
            }

            LoadCurrentConfiguration(options as DocSetOptions);

            bool returnSuccess = true;

            switch (invokedVerb)
            {
                case CommandLineOptions.VerbPrint:
                    await PrintDocInformationAsync((PrintOptions)options);
                    break;
                case CommandLineOptions.VerbCheckLinks:
                    returnSuccess = await CheckLinksAsync((DocSetOptions)options);
                    break;
                case CommandLineOptions.VerbDocs:
                    returnSuccess = await CheckDocsAsync((BasicCheckOptions)options);
                    break;
                case CommandLineOptions.VerbCheckAll:
                    returnSuccess = await CheckDocsAllAsync((BasicCheckOptions)options);
                    break;
                case CommandLineOptions.VerbService:
                    returnSuccess = await CheckServiceAsync((CheckServiceOptions)options);
                    break;
                case CommandLineOptions.VerbPublish:
                    returnSuccess = await PublishDocumentationAsync((PublishOptions)options);
                    break;
                case CommandLineOptions.VerbMetadata:
                    await CheckServiceMetadataAsync((CheckMetadataOptions)options);
                    break;
                case CommandLineOptions.VerbAbout:
                    PrintAboutMessage();
                    Exit(failure: false);
                    break;
            }

            Exit(failure: !returnSuccess);
        }

        /// <summary>
        /// Perform all of the local documentation based checks. This is the "compile"
        /// command for the documentation that verifies that everything is clean inside the 
        /// documentation itself.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static async Task<bool> CheckDocsAllAsync(BasicCheckOptions options)
        {
            var docset = await GetDocSetAsync(options);

            if (null == docset)
                return false;

            var checkLinksResult = await CheckLinksAsync(options, docset);
            var checkDocsResults = await CheckDocsAsync(options, docset);

            return checkLinksResult && checkDocsResults;
        }

        private static void PrintAboutMessage()
        {
            FancyConsole.WriteLine();
            FancyConsole.WriteLine(ConsoleColor.Cyan, "apidocs.exe - API Documentation Test Tool");
            FancyConsole.WriteLine(ConsoleColor.Cyan, "Copyright (c) 2015 Microsoft Corporation");
            FancyConsole.WriteLine();
            FancyConsole.WriteLine(ConsoleColor.Cyan, "For more information see http://github.com/onedrive/markdown-scanner/");
            FancyConsole.WriteLine();
        }


        /// <summary>
        /// Create and return a document set based on input options
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static async Task<DocSet> GetDocSetAsync(DocSetOptions options)
        {
            FancyConsole.VerboseWriteLine("Opening documentation from {0}", options.DocumentationSetPath);
            DocSet set = new DocSet(options.DocumentationSetPath);

            FancyConsole.VerboseWriteLine("Scanning documentation files...");
            ValidationError[] loadErrors;
            if (!set.ScanDocumentation(out loadErrors))
            {
                await WriteOutErrorsAndFinishTestAsync(loadErrors, options.SilenceWarnings);
                return null;
            }
                
            return set;
        }

        public static void RecordWarning(string format, params object[] variables)
        {
            var message = string.Format(format, variables);
            FancyConsole.WriteLine(FancyConsole.ConsoleWarningColor, message);
            Task.Run(() => BuildWorker.AddMessageAsync(message, MessageCategory.Warning));
        }

        public static void RecordError(string format, params object[] variables)
        {
            var message = string.Format(format, variables);
            FancyConsole.WriteLine(FancyConsole.ConsoleErrorColor, message);
            Task.Run(() => BuildWorker.AddMessageAsync(message, MessageCategory.Error));
        }

        /// <summary>
        /// Output information about the document set to the console
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task PrintDocInformationAsync(PrintOptions options, DocSet docset = null)
        {
            docset = docset ?? await GetDocSetAsync(options);
            if (null == docset)
            {
                return;
            }

            if (options.PrintFiles)
            {
                await PrintFilesAsync(options, docset);
            }
            if (options.PrintResources)
            {
                await PrintResourcesAsync(options, docset);
            }
            if (options.PrintMethods)
            {
                await PrintMethodsAsync(options, docset);
            }
        }

#region Print verb commands
        /// <summary>
        /// Prints a list of the documentation files in a docset to the console.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task PrintFilesAsync(DocSetOptions options, DocSet docset)
        {
            docset = docset ?? await GetDocSetAsync(options);
            if (null == docset)
                return;


            if (null == docset)
                return;

            FancyConsole.WriteLine();
            FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Documentation files");

            string format = null;
            if (options.EnableVerboseOutput)
            {
                format = "{1} (resources: {2}, methods: {3})";
            }
            else
            {
                format = "{0} (r:{2}, m:{3})";
            }

            foreach (var file in docset.Files)
            {
                ConsoleColor color = FancyConsole.ConsoleSuccessColor;
                if (file.Resources.Length == 0 && file.Requests.Length == 0)
                    color = FancyConsole.ConsoleWarningColor;

                FancyConsole.WriteLineIndented("  ", color, format, file.DisplayName, file.FullPath, file.Resources.Length, file.Requests.Length);
            }
        }

        /// <summary>
        /// Print a list of the resources detected in the documentation set to the console.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task PrintResourcesAsync(DocSetOptions options, DocSet docset)
        {
            docset = docset ?? await GetDocSetAsync(options);
            if (null == docset)
                return;

            FancyConsole.WriteLine();
            FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Defined resources:");
            FancyConsole.WriteLine();

            var sortedResources = docset.Resources.OrderBy(x => x.ResourceType);

            foreach (var resource in sortedResources)
            {


                if (options.EnableVerboseOutput)
                {
                    string metadata = JsonConvert.SerializeObject(resource.Metadata);
                    FancyConsole.Write("  ");
                    FancyConsole.Write(FancyConsole.ConsoleHeaderColor, resource.ResourceType);
                    FancyConsole.WriteLine(" flags: {1}", resource.ResourceType, metadata);
                }
                else
                {
                    FancyConsole.WriteLineIndented("  ", FancyConsole.ConsoleHeaderColor, resource.ResourceType);
                }

                FancyConsole.WriteLineIndented("    ", FancyConsole.ConsoleCodeColor, resource.JsonExample);
                FancyConsole.WriteLine();
            }
        }

        /// <summary>
        /// Print a list of the methods (request/responses) discovered in the documentation to the console.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task PrintMethodsAsync(DocSetOptions options, DocSet docset)
        {
            docset = docset ?? await GetDocSetAsync(options);
            if (null == docset)
                return;

            FancyConsole.WriteLine();
            FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Defined methods:");
            FancyConsole.WriteLine();

            foreach (var method in docset.Methods)
            {
                FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Method '{0}' in file '{1}'", method.Identifier, method.SourceFile.DisplayName);

                var requestMetadata = options.EnableVerboseOutput ? JsonConvert.SerializeObject(method.RequestMetadata) : string.Empty;
                FancyConsole.WriteLineIndented("  ", FancyConsole.ConsoleSubheaderColor, "Request: {0}", requestMetadata);
                FancyConsole.WriteLineIndented("    ", FancyConsole.ConsoleCodeColor, method.Request);

                if (options.EnableVerboseOutput)
                {
                    FancyConsole.WriteLine();
                    var responseMetadata = JsonConvert.SerializeObject(method.ExpectedResponseMetadata);
                    FancyConsole.WriteLineIndented("  ", FancyConsole.ConsoleSubheaderColor, "Expected Response: {0}", responseMetadata);
                    FancyConsole.WriteLineIndented("    ", FancyConsole.ConsoleCodeColor, method.ExpectedResponse);
                    FancyConsole.WriteLine();
                }
                FancyConsole.WriteLine();
            }
        }

#endregion


        /// <summary>
        /// Verifies that all markdown links inside the documentation are valid. Prints warnings
        /// to the console for any invalid links. Also checks for orphaned documents.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docs"></param>
        private static async Task<bool> CheckLinksAsync(DocSetOptions options, DocSet docs = null)
        {
            const string testName = "Check-links";
            var docset = docs ?? await GetDocSetAsync(options);

            if (null == docset)
                return false;


            TestReport.StartTest(testName);

            ValidationError[] errors;
            docset.ValidateLinks(options.EnableVerboseOutput, out errors);

            foreach (var error in errors)
            {
                MessageCategory category;
                if (error.IsWarning)
                    category = MessageCategory.Warning;
                else if (error.IsError)
                    category = MessageCategory.Error;
                else
                    category = MessageCategory.Information;

                string message = string.Format("{1}: {0}", error.Message.FirstLineOnly(), error.Code);
                await TestReport.LogMessageAsync(message, category);
            }

            return await WriteOutErrorsAndFinishTestAsync(errors, options.SilenceWarnings, successMessage: "No link errors detected.", testName: testName);
        }


        private static MethodDefinition LookUpMethod(DocSet docset, string methodName)
        {
            var query = from m in docset.Methods
                        where m.Identifier.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                        select m;

            return query.FirstOrDefault();
        }


        /// <summary>
        /// Perform internal consistency checks on the documentation, including verify that 
        /// code blocks have proper formatting, that resources are used properly, and that expected
        /// responses and examples conform to the resource definitions.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docs"></param>
        /// <returns></returns>
        private static async Task<bool> CheckDocsAsync(BasicCheckOptions options, DocSet docs = null)
        {
            var docset = docs ?? await GetDocSetAsync(options);
            if (null == docset)
                return false;

            FancyConsole.WriteLine();

            var resultMethods = await CheckMethodsAsync(options, docset);
            CheckResults resultExamples = new CheckResults();
            if (string.IsNullOrEmpty(options.MethodName))
            {
                resultExamples = await CheckExamplesAsync(options, docset);
            }

            var combinedResults = resultMethods + resultExamples;

            if (options.IgnoreWarnings)
            {
                combinedResults.ConvertWarningsToSuccess();
            }

            combinedResults.PrintToConsole();

            return combinedResults.FailureCount == 0;
        }

        /// <summary>
        /// Perform an internal consistency check on the examples defined in the documentation. Prints
        /// the results of the tests to the console.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task<CheckResults> CheckExamplesAsync(BasicCheckOptions options, DocSet docset)
        {
            var results = new CheckResults();

            foreach (var doc in docset.Files)
            {
                if (doc.Examples.Length == 0)
                {
                    continue;
                }

                FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Checking examples in \"{0}\"...", doc.DisplayName);

                foreach (var example in doc.Examples)
                {
                    if (example.Metadata == null)
                        continue;
                    if (example.Language != CodeLanguage.Json)
                        continue;

                    var testName = string.Format("check-example: {0}", example.Metadata.MethodName, example.Metadata.ResourceType);
                    TestReport.StartTest(testName, doc.DisplayName);

                    ValidationError[] errors;
                    docset.ResourceCollection.ValidateJsonExample(example.Metadata, example.SourceExample, out errors);

                    await WriteOutErrorsAndFinishTestAsync(errors, options.SilenceWarnings, "   ", "No errors.", false, testName, "Warnings detected", "Errors detected");
                    results.IncrementResultCount(errors);
                }
            }

            return results;
        }

        /// <summary>
        /// Performs an internal consistency check on the methods (requests/responses) in the documentation.
        /// Prints the results of the tests to the console.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static async Task<CheckResults> CheckMethodsAsync(BasicCheckOptions options, DocSet docset)
        {
            MethodDefinition[] methods = FindTestMethods(options, docset);
            CheckResults results = new CheckResults();

            foreach (var method in methods)
            {
                var testName = "check-method-syntax: " + method.Identifier;
                TestReport.StartTest(testName, method.SourceFile.DisplayName);

                if (string.IsNullOrEmpty(method.ExpectedResponse))
                {
                    await TestReport.FinishTestAsync(testName, TestOutcome.Failed, "Null response where one was expected.");
                    results.FailureCount++;
                    continue;
                }

                var parser = new HttpParser();
                var expectedResponse = parser.ParseHttpResponse(method.ExpectedResponse);

                ValidationError[] errors;
                method.ValidateResponse(expectedResponse, null, null, out errors);

                await WriteOutErrorsAndFinishTestAsync(errors, options.SilenceWarnings, "   ", "No errors.", false, testName, "Warnings detected", "Errors detected");
                results.IncrementResultCount(errors);
            }

            return results;
        }

        /// <summary>
        /// Parse the command line parameters into a set of methods that match the command line parameters.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="docset"></param>
        /// <returns></returns>
        private static MethodDefinition[] FindTestMethods(BasicCheckOptions options, DocSet docset)
        {
            MethodDefinition[] methods = null;
            if (!string.IsNullOrEmpty(options.MethodName))
            {
                var foundMethod = LookUpMethod(docset, options.MethodName);
                if (null == foundMethod)
                {
                    FancyConsole.WriteLine(FancyConsole.ConsoleErrorColor, "Unable to locate method '{0}' in docset.", options.MethodName);
                    Exit(failure: true);
                }
                methods = new MethodDefinition[] { LookUpMethod(docset, options.MethodName) };
            }
            else if (!string.IsNullOrEmpty(options.FileName))
            {
                var selectedFileQuery = from f in docset.Files where f.DisplayName == options.FileName select f;
                var selectedFile = selectedFileQuery.SingleOrDefault();
                if (selectedFile == null)
                {
                    FancyConsole.WriteLine(FancyConsole.ConsoleErrorColor, "Unable to locate file '{0}' in docset.", options.FileName);
                    Exit(failure: true);
                }
                methods = selectedFile.Requests;
            }
            else
            {
                methods = docset.Methods;
            }
            return methods;
        }


        /// <summary>
        /// Print a collection of ValidationError objects to the console. Also shuts down the test runner
        /// for the current test.
        /// </summary>
        /// <param name="errors"></param>
        /// <param name="silenceWarnings"></param>
        /// <param name="indent"></param>
        /// <param name="successMessage"></param>
        /// <param name="endLineBeforeWriting"></param>
        /// <param name="testName"></param>
        /// <param name="warningsMessage"></param>
        /// <param name="errorsMessage"></param>
        /// <returns></returns>
        private static async Task<bool> WriteOutErrorsAndFinishTestAsync(IEnumerable<ValidationError> errors, bool silenceWarnings, string indent = "", string successMessage = null, bool endLineBeforeWriting = false, string testName = null, string warningsMessage = null, string errorsMessage = null)
        {
            var validationErrors = errors as ValidationError[] ?? errors.ToArray();
            WriteMessages(validationErrors, silenceWarnings, indent, endLineBeforeWriting);

            TestOutcome outcome = TestOutcome.None;
            string outputMessage = null;

            var errorMessages = validationErrors.Where(x => x.IsError);
            var warningMessages = validationErrors.Where(x => x.IsWarning);
                        
            if (errorMessages.Any())
            {
                // Write failure message
                var singleError = errorMessages.First();
                if (errorMessages.Count() == 1)
                    outputMessage = singleError.Message.FirstLineOnly();
                else
                    outputMessage = "Multiple errors occured.";

                outcome = TestOutcome.Failed;
            }
            else if (!silenceWarnings && warningMessages.Any())
            {
                // Write warning message
                var singleWarning = warningMessages.First();
                if (warningMessages.Count() == 1)
                    outputMessage = singleWarning.Message.FirstLineOnly();
                else
                    outputMessage = "Multiple warnings occured.";
                outcome = TestOutcome.Passed;
            }
            else
            {
                // write success message!
                outputMessage = successMessage;
                outcome = TestOutcome.Passed;
            }

            // Record this test's outcome in the build worker API
            if (null != testName)
            {
                var errorMessage = (from e in validationErrors select e.ErrorText).ComponentsJoinedByString("\r\n");
                await TestReport.FinishTestAsync(testName, outcome, outputMessage, stdOut: errorMessage);
            }

            return outcome == TestOutcome.Passed;
        }

        /// <summary>
        /// Prints ValidationError messages to the console, optionally excluding warnings and messages.
        /// </summary>
        /// <param name="validationErrors"></param>
        /// <param name="errorsOnly"></param>
        /// <param name="indent"></param>
        /// <param name="endLineBeforeWriting"></param>
        private static void WriteMessages(ValidationError[] validationErrors, bool errorsOnly = false, string indent = "", bool endLineBeforeWriting = false)
        {
            foreach (var error in validationErrors)
            {
                // Skip messages if verbose output is off
                if (!error.IsWarning && !error.IsError && !FancyConsole.WriteVerboseOutput)
                {
                    continue;
                }

                // Skip warnings if silence warnings is enabled.
                if (errorsOnly && !error.IsError)
                {
                    continue;
                }

                if (endLineBeforeWriting)
                {
                    FancyConsole.WriteLine();
                }

                WriteValidationError(indent, error);
            }
        }

        /// <summary>
        /// Prints a formatted ValidationError object to the console.
        /// </summary>
        /// <param name="indent"></param>
        /// <param name="error"></param>
        internal static void WriteValidationError(string indent, ValidationError error)
        {
            ConsoleColor color;
            if (error.IsWarning)
            {
                color = FancyConsole.ConsoleWarningColor;
            }
            else if (error.IsError)
            {
                color = FancyConsole.ConsoleErrorColor;
            }
            else
            {
                color = FancyConsole.ConsoleDefaultColor;
            }

            FancyConsole.WriteLineIndented(indent, color, error.ErrorText);
        }

        /// <summary>
        /// Executes the remote service tests defined in the documentation. This is similar to CheckDocs, expect
        /// that the actual requests come from the service instead of the documentation. Prints the errors to 
        /// the console.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static async Task<bool> CheckServiceAsync(CheckServiceOptions options)
        {
            // See if we're supposed to run check-service on this branch (assuming we know what branch we're running on)
            if (!string.IsNullOrEmpty(options.BranchName))
            {
                string[] validBranches = null;
                if (null != CurrentConfiguration) 
                    validBranches = CurrentConfiguration.CheckServiceEnabledBranches;

                if (null != validBranches && !validBranches.Contains(options.BranchName))
                {
                    RecordWarning("Aborting check-service run. Branch \"{0}\" wasn't in the checkServiceEnabledBranches configuration list.", options.BranchName);
                    return true;
                }
            }

            var docset = await GetDocSetAsync(options);
            if (null == docset)
            {
                return false;
            }

            FancyConsole.WriteLine();

            if (!string.IsNullOrEmpty(options.ODataMetadataLevel))
            {
                ValidationConfig.ODataMetadataLevel = options.ODataMetadataLevel;
            }

            if (options.FoundAccounts == null || !options.FoundAccounts.Any())
            {
                RecordError("No account was found. Cannot connect to the service.");
                return false;
            }

            var accountsToProcess =
                options.FoundAccounts.Where(
                    x => string.IsNullOrEmpty(options.AccountName)
                        ? x.Enabled
                        : options.AccountName.Equals(x.Name));
            
            var methods = FindTestMethods(options, docset);

            bool allSuccessful = true;
            foreach (var account in accountsToProcess)
            {
                allSuccessful &= await CheckMethodsForAccountAsync(options, account, methods, docset);
            }
            return allSuccessful;
        }

        /// <summary>
        /// Execute the provided methods on the given account.
        /// </summary>
        /// <param name="options"></param>
        /// <param name="account"></param>
        /// <param name="methods"></param>
        /// <param name="docset"></param>
        /// <returns>True if the methods all passed, false if there were failures.</returns>
        private static async Task<bool> CheckMethodsForAccountAsync(CheckServiceOptions options, IServiceAccount account, MethodDefinition[] methods, DocSet docset)
        {
            //CheckResults results = new CheckResults();

            ConfigureAdditionalHeadersForAccount(options, account);

            string testNamePrefix = account.Name.ToLower() + ": ";
            FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Testing with account: {0}", account.Name);

            try
            {
                await account.PrepareForRequestAsync();
            }
            catch (Exception ex)
            {
                RecordError(ex.Message);
                return false;
            }

            AuthenicationCredentials credentials = account.CreateCredentials();
            int concurrentTasks = options.ParallelTests ? ParallelTaskCount : 1;

            CheckResults docSetResults = new CheckResults();

            await ForEachAsync(methods, concurrentTasks, async method => {
                ScenarioDefinition[] scenarios = docset.TestScenarios.ScenariosForMethod(method);
                ValidationResults results = await method.ValidateServiceResponseAsync(scenarios, account, credentials);

                PrintResultsToConsole(method, account, results, options);
                await TestReport.LogMethodTestResults(method, account, results);
                docSetResults.RecordResults(results, options);
                
                if (concurrentTasks == 1)
                    AddPause(options);
            });

            if (options.IgnoreWarnings || options.SilenceWarnings)
            {
                // Remove the warning flag from the outcomes
                docSetResults.ConvertWarningsToSuccess();
            }

            docSetResults.PrintToConsole();

            bool hadWarnings = docSetResults.WarningCount > 0;
            bool hadErrors = docSetResults.FailureCount > 0;

            return !(hadErrors | hadWarnings);
        }

        

        /// <summary>
        /// Parallel enabled for each processor that supports async lambdas. Copied from 
        /// http://blogs.msdn.com/b/pfxteam/archive/2012/03/05/10278165.aspx
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="source">Collection of items to iterate over</param>
        /// <param name="dop">Degree of parallelism to execute.</param>
        /// <param name="body">Lambda expression executed for each operation</param>
        /// <returns></returns>
        public static Task ForEachAsync<T>(IEnumerable<T> source, int dop, Func<T, Task> body)
        {
            return Task.WhenAll(
                from partition in System.Collections.Concurrent.Partitioner.Create(source).GetPartitions(dop)
                select Task.Run(async delegate
                {
                    using (partition)
                        while (partition.MoveNext())
                            await body(partition.Current);
                }));
        }

        /// <summary>
        /// Write the results of a test to the output console.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="account"></param>
        /// <param name="results"></param>
        /// <param name="options"></param>
        private static void PrintResultsToConsole(MethodDefinition method, IServiceAccount account, ValidationResults output, CheckServiceOptions options)
        {
            // Only allow one thread at a time to write to the console so we don't interleave our results.
            lock (typeof(Program))
            {
                FancyConsole.WriteLine(
                    FancyConsole.ConsoleHeaderColor,
                    "Testing method {0} with account {1}",
                    method.Identifier,
                    account.Name);

                foreach (var scenario in output.Results)
                {
                    if (scenario.Errors.Count > 0)
                    {
                        FancyConsole.WriteLineIndented(
                            "  ",
                            FancyConsole.ConsoleSubheaderColor,
                            "Scenario: {0}",
                            scenario.Name);

                        foreach (var message in scenario.Errors)
                        {
                            if (options.EnableVerboseOutput || message.IsWarningOrError)
                                FancyConsole.WriteLineIndented(
                                    "    ",
                                    FancyConsole.ConsoleDefaultColor,
                                    message.ErrorText);
                        }

                        if (options.SilenceWarnings && scenario.Outcome == ValidationOutcome.Warning)
                        {
                            scenario.Outcome = ValidationOutcome.Passed;
                        }

                        FancyConsole.WriteLineIndented(
                            "    ",
                            scenario.Outcome.ConsoleColor(),
                            "Scenario finished with outcome: {0}. Duration: {1}",
                            scenario.Outcome,
                            scenario.Duration);
                    }
                }

                FancyConsole.WriteLineIndented(
                    "  ",
                    output.OverallOutcome.ConsoleColor(),
                    "Method testing finished with overall outcome: {0}",
                    output.OverallOutcome);
                FancyConsole.WriteLine();
            }
        }




        private static void ConfigureAdditionalHeadersForAccount(CheckServiceOptions options, IServiceAccount account)
        {
            if (account.AdditionalHeaders != null && account.AdditionalHeaders.Length > 0)
            {
                // If the account needs additional headers, merge them in.
                List<string> headers = new List<string>(account.AdditionalHeaders);
                if (options.AdditionalHeaders != null)
                {
                    headers.AddRange(options.AdditionalHeaders.Split('|'));
                }
                ValidationConfig.AdditionalHttpHeaders = headers.ToArray();
            }
            else if (options.AdditionalHeaders != null)
            {
                var headers = options.AdditionalHeaders.Split('|');
                ValidationConfig.AdditionalHttpHeaders = headers.ToArray();
            }
        }

        private static void Exit(bool failure)
        {
            var exitCode = failure ? ExitCodeFailure : ExitCodeSuccess;
#if DEBUG
            Console.WriteLine("Exit code: " + exitCode);
            if (Debugger.IsAttached)
            {
                Console.WriteLine();
                Console.Write("Press any key to exit.");
                Console.ReadKey();
            }
#endif

            Environment.Exit(exitCode);
        }


       

        private static void AddPause(CheckServiceOptions options)
        {
            if (options.PauseBetweenRequests)
            {
                FancyConsole.WriteLine("Press any key to continue");
                Console.ReadKey();
                FancyConsole.WriteLine();
            }
        }

      

        private static async Task<bool> PublishDocumentationAsync(PublishOptions options)
        {
            var outputPath = options.OutputDirectory;

            FancyConsole.WriteLine("Publishing documentation to {0}", outputPath);

            DocSet docs = await GetDocSetAsync(options);
            if (null == docs)
                return false;

            DocumentPublisher publisher = null;
            switch (options.Format)
            {
                case PublishOptions.PublishFormat.Markdown:
                    publisher = new MarkdownPublisher(docs);
                    break;
                case PublishOptions.PublishFormat.Html:
                    publisher = new DocumentPublisherHtml(docs, options);
                    break;
                case PublishOptions.PublishFormat.Mustache:
                    publisher = new HtmlMustacheWriter(docs, options);
                    break;
                case PublishOptions.PublishFormat.Swagger2:
                    publisher = new SwaggerWriter(docs, "https://service.org")  // TODO: Plumb in the base URL.
                    {
                        Title = options.Title,
                        Description = options.Description,
                        Version = options.Version
                    };
                    break;
                case PublishOptions.PublishFormat.Outline:
                    publisher = new OutlinePublisher(docs);
                    break;
                default:
                    FancyConsole.WriteLine(
                        FancyConsole.ConsoleErrorColor,
                        "Unsupported publishing format: {0}",
                        options.Format);
                    return false;
            }

            FancyConsole.WriteLineIndented("  ", "Format: {0}", publisher.GetType().Name);
            publisher.VerboseLogging = options.EnableVerboseOutput;
            FancyConsole.WriteLine();
            
            FancyConsole.WriteLine("Publishing content...");
            publisher.NewMessage += publisher_NewMessage;

            try
            {
                await publisher.PublishToFolderAsync(outputPath);
                FancyConsole.WriteLine(FancyConsole.ConsoleSuccessColor, "Finished publishing documentation to: {0}", outputPath);
            }
            catch (Exception ex)
            {
                FancyConsole.WriteLine(
                    FancyConsole.ConsoleErrorColor,
                    "An error occured while publishing: {0}",
                    ex.Message);
                FancyConsole.VerboseWriteLine(ex.ToString());
                return false;
            }

            return true;
        }

        static void publisher_NewMessage(object sender, ValidationMessageEventArgs e)
        {
            var msg = e.Message;
            if (!FancyConsole.WriteVerboseOutput && !msg.IsError && !msg.IsWarning)
                return;

            WriteValidationError("", msg);
        }


        private static async Task<List<Schema>> TryGetMetadataSchemasAsync(CheckMetadataOptions options)
        {
            if (string.IsNullOrEmpty(options.ServiceMetadataLocation))
            {
                RecordError("No service metadata file location specified.");
                return null;
            }

            FancyConsole.WriteLine(FancyConsole.ConsoleHeaderColor, "Loading service metadata from '{0}'...", options.ServiceMetadataLocation);

            List<Schema> schemas;
            try
            {
                Uri metadataUrl;
                if (Uri.TryCreate(options.ServiceMetadataLocation, UriKind.Absolute, out metadataUrl))
                {
                    schemas = await ODataParser.ReadSchemaFromMetadataUrlAsync(metadataUrl);
                }
                else
                {
                    schemas = await ODataParser.ReadSchemaFromFileAsync(options.ServiceMetadataLocation);
                }
            }
            catch (Exception ex)
            {
                RecordError("Error parsing service metadata: {0}", ex.Message);
                return null;
            }

            return schemas;
        }

        /// <summary>
        /// Validate that the CSDL metadata defined for a service matches the documentation.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static async Task<bool> CheckServiceMetadataAsync(CheckMetadataOptions options)
        {
            List<Schema> schemas = await TryGetMetadataSchemasAsync(options);
            if (null == schemas)
                return false;

            FancyConsole.WriteLine(FancyConsole.ConsoleSuccessColor, "  found {0} schema definitions: {1}", schemas.Count, (from s in schemas select s.Namespace).ComponentsJoinedByString(", "));

            var docSet = await GetDocSetAsync(options);
            if (null == docSet)
                return false;

            const string testname = "validate-service-metadata";
            TestReport.StartTest(testname);

            List<ResourceDefinition> foundResources = ODataParser.GenerateResourcesFromSchemas(schemas);
            CheckResults results = new CheckResults();

            List<ValidationError> collectedErrors = new List<ValidationError>();

            foreach (var resource in foundResources)
            {
                FancyConsole.WriteLine();
                FancyConsole.Write(FancyConsole.ConsoleHeaderColor, "Checking resource: {0}...", resource.Metadata.ResourceType);

                FancyConsole.VerboseWriteLine();
                FancyConsole.VerboseWriteLine(resource.JsonExample);
                FancyConsole.VerboseWriteLine();

                // Verify that this resource matches the documentation
                ValidationError[] errors;
                docSet.ResourceCollection.ValidateJsonExample(resource.Metadata, resource.JsonExample, out errors, new ValidationOptions { RelaxedStringValidation = true });
                results.IncrementResultCount(errors);

                collectedErrors.AddRange(errors);

                await WriteOutErrorsAndFinishTestAsync(errors, options.SilenceWarnings, successMessage: " no errors.");
            }

            if (options.IgnoreWarnings)
            {
                results.ConvertWarningsToSuccess();
            }

            var output = (from e in collectedErrors select e.ErrorText).ComponentsJoinedByString("\r\n");

            await TestReport.FinishTestAsync(testname, results.WereFailures ? TestOutcome.Failed : TestOutcome.Passed, stdOut:output);

            results.PrintToConsole();
            return !results.WereFailures;
        }
    }

    class ConsoleAppLogger : ILogHelper
    {
        public void RecordError(ValidationError error)
        {
            Program.WriteValidationError(string.Empty, error);
        }
    }

    internal static class OutcomeExtensionMethods
    {

        public static ConsoleColor ConsoleColor(this ValidationOutcome outcome)
        {
            if ((outcome & ValidationOutcome.Error) > 0)
                return FancyConsole.ConsoleErrorColor;
            if ((outcome & ValidationOutcome.Warning) > 0)
                return FancyConsole.ConsoleWarningColor;
            if ((outcome & ValidationOutcome.Passed) > 0)
                return FancyConsole.ConsoleSuccessColor;
            if ((outcome & ValidationOutcome.Skipped) > 0)
                return FancyConsole.ConsoleWarningColor;

            return FancyConsole.ConsoleDefaultColor;

        }

    }

}