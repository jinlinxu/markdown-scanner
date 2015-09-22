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

namespace ApiDocs.Validation
{
    using System;
    using System.Diagnostics;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;

    public class CodeBlockAnnotation
    {
        /// <summary>
        /// The OData type name of the resource
        /// </summary>
        [JsonProperty("@odata.type", NullValueHandling=NullValueHandling.Ignore )]
        public string ResourceType { get; set; }

        /// <summary>
        /// Type of code block
        /// </summary>
        [JsonProperty("blockType", Order=-2), JsonConverter(typeof(StringEnumConverter))]
        public CodeBlockType BlockType { get; set; }

        /// <summary>
        /// Specify the name of properties in the schema which are optional
        /// </summary>
        [JsonProperty("optionalProperties", NullValueHandling=NullValueHandling.Ignore)]
        public string[] OptionalProperties { get; set; }

        /// <summary>
        /// Specify that the result is a collection of the resource type instead of a single instance.
        /// </summary>
        [JsonProperty("isCollection", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsCollection { get; set; }

        /// <summary>
        /// Specify the name of the property that holds the array of items.
        /// </summary>
        [JsonProperty("collectionProperty", DefaultValueHandling=DefaultValueHandling.Ignore)]
        public string CollectionPropertyName { get; set; }

        /// <summary>
        /// Indicates that the response is empty (has no value)
        /// </summary>
        [JsonProperty("isEmpty", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsEmpty { get; set; }

        /// <summary>
        /// Specifies that the example is truncated and should not generate warnings about 
        /// missing fields unless those fields are shown in the example.
        /// </summary>
        [JsonProperty("truncated", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool TruncatedResult { get; set; }

        /// <summary>
        /// The name of the request / response method.
        /// </summary>
        [JsonProperty("name", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string MethodName { get; set; }

        /// <summary>
        /// Indicates that the response is expected to be an error response.
        /// </summary>
        [JsonProperty("expectError", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool ExpectError { get; set; }

        /// <summary>
        /// By default all properties are expected to be non-null values. If a null value is returned
        /// in the JSON ("foo": null) an error is generated. This can be used on a resource to allow
        /// some properties to be returned as null.
        /// </summary>
        [JsonProperty("nullableProperties", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string[] NullableProperties { get; set; }

        /// <summary>
        /// When provided indicates that the response is a long running operation that will return an 
        /// asyncJobStatus response from a Location URL. When the job is complete, a Location URL will
        /// be returned that returns a response with the resource type indicated by this property.
        /// </summary>
        [JsonProperty("longRunningResponseType", DefaultValueHandling =  DefaultValueHandling.Ignore)]
        public string LongRunningResponseType { get; set; }

        /// <summary>
        /// Convert a JSON string into an instance of this class
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        public static CodeBlockAnnotation FromJson(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<CodeBlockAnnotation>(json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Error parsing JSON annotation: " + ex.Message);
                return new CodeBlockAnnotation() { BlockType = CodeBlockType.Ignored };
            }
        }
    }

    public enum CodeBlockType
    {
        /// <summary>
        /// Default value that indicates parsing failed.
        /// </summary>
        Unknown,

        /// <summary>
        /// Resource type definition
        /// </summary>
        Resource,

        /// <summary>
        /// Raw HTTP request to the API
        /// </summary>
        Request,

        /// <summary>
        /// Raw HTTP response from the API
        /// </summary>
        Response,

        /// <summary>
        /// Ignored code block. No processing is done
        /// </summary>
        Ignored,

        /// <summary>
        /// Example code block. Should be checked for JSON correctness and resources
        /// </summary>
        Example,

        /// <summary>
        /// A simulated response, used for unit testing.
        /// </summary>
        SimulatedResponse,

        /// <summary>
        /// A block representing a test parameter definition for the preceding example
        /// </summary>
        TestParams,
    }
}