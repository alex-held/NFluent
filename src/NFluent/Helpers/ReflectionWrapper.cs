﻿#region File header

// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ExtendedFileInfo.cs" company="">
//   Copyright 2014 Cyrille DUPUYDAUBY, Thomas PIERRAIN
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//       http://www.apache.org/licenses/LICENSE-2.0
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

#endregion

namespace NFluent.Helpers
{
    using System;
    using System.Collections.Generic;
#if NETSTANDARD1_3
    using System.Reflection;
#endif
    using System.Text.RegularExpressions;
    using Extensions;
    using static System.String;

    /// <summary>
    /// This class wraps instances for reflection based checks (in NFluent).
    /// </summary>
    public class ReflectionWrapper
    {
        private readonly string nameInSource;
        private readonly string prefix;
        private readonly string labelPattern;

        private ReflectionWrapper(string nameInSource, string prefix, string labelPattern, Type type, object value, Criteria criteria)
        {
            this.nameInSource = nameInSource;
            this.prefix = prefix;
            this.labelPattern = labelPattern;
            this.Criteria = criteria;
            this.ValueType = type;
            this.SetValue(value);
        }

        internal string MemberLongName => IsNullOrEmpty(this.prefix)
            ? this.nameInSource
            : $"{this.prefix}.{this.nameInSource}";

        internal Criteria Criteria { get; set; }

        internal string MemberLabel => Format(this.labelPattern, this.MemberLongName);

        internal object Value { get; private set; }

        internal Type ValueType { get; set; }

        internal bool IsArray => this.ValueType.IsArray;

        internal static ReflectionWrapper BuildFromInstance(Type type, object value, Criteria criteria)
        {
            return new ReflectionWrapper(Empty, Empty, "instance", type, value, criteria);
        }

        internal static ReflectionWrapper BuildFromField(string prefix, string name, Type type, object value, Criteria criteria)
        {
            string labelPattern;

            if (EvaluateCriteria(AutoPropertyMask, name, out var nameInSource))
            {
                labelPattern = $"autoproperty '{{0}}' (field '{name}')";
            }
            else if (EvaluateCriteria(AnonymousTypeFieldMask, name, out nameInSource))
            {
                labelPattern = "field '{0}'";
            }
            else
            {
                nameInSource = name;
                labelPattern = "field '{0}'";
            }

            return new ReflectionWrapper(nameInSource, prefix, labelPattern, value?.GetType()?? type, value, criteria);
       }

        internal static ReflectionWrapper BuildFromProperty(string prefix, string name, Type type, object value, Criteria criteria)
        {
            return new ReflectionWrapper(name, prefix, "property '{0}'", value?.GetType()?? type, value, criteria);
        }

        internal void SetValue(object obj)
        {
            this.Value = obj;
        }

        internal bool ChecksIfImplementsEqual()
        {
            return this.ValueType.ImplementsEquals();
        }

        internal List<MemberMatch> CompareValue(
            ReflectionWrapper actualFieldDescription,
            IList<object> scanned,
            int depth)
        {
            var result = new List<MemberMatch>();
            if (this.Value != null && scanned.Contains(this.Value))
            {
                return result;
            }

            if (depth <= 0 && this.ChecksIfImplementsEqual())
            {
                result.Add(new MemberMatch(this, actualFieldDescription));
            }
            else
            {
                scanned.Add(this.Value);
                if (this.Value == null || actualFieldDescription.Value == null)
                {
                    result.Add(new MemberMatch(this, actualFieldDescription));
                }
                else if (this.IsArray)
                {
                    var array = (Array) this.Value;
                    var actualArray = (Array) actualFieldDescription.Value;
                    if (actualArray.Length != array.Length)
                    {
                        result.Add(new MemberMatch(this, actualFieldDescription));
                    }
                    else
                    {
                        result.AddRange(
                            this.ScanFields(
                                actualFieldDescription,
                                scanned,
                                depth - 1));
                    }
                }
                else
                {
                    result.AddRange(
                        this.ScanFields(
                            actualFieldDescription,
                            scanned,
                            depth - 1));
                }
            }

            return result;
        }

        private IEnumerable<MemberMatch> ScanFields(ReflectionWrapper actual, IList<object> scanned, int depth)
        {
            var result = new List<MemberMatch>();

            foreach (var member in this.GetSubExtendedMemberInfosFields())
            {
                var actualFieldMatching = actual.FindMember(member);

                // field not found in SUT
                if (actualFieldMatching == null)
                {
                    result.Add(new MemberMatch(member, null));
                     continue;
                }

                result.AddRange(member.CompareValue(actualFieldMatching, scanned, depth - 1));
            }

            return result;
        }

        private IEnumerable<ReflectionWrapper> GetSubExtendedMemberInfosFields()
        {
            var result = new List<ReflectionWrapper>();
            if (this.IsArray)
            {
                var array = (Array) this.Value;
                var fieldType = array.GetType().GetElementType();
                for (var i = 0; i < array.Length; i++)
                {
                    var expectedEntryDescription = BuildFromField(this.MemberLongName, $"[{i}]", fieldType, array.GetValue(i), this.Criteria);
                    result.Add(expectedEntryDescription);
                }
            }
            else
            {
                var currentType = this.ValueType;
                while (currentType != null)
                {
                    if (this.Criteria.WithFields)
                    {
                        var fieldsInfo = currentType.GetFields(this.Criteria.BindingFlags);
                        foreach (var info in fieldsInfo)
                        {
                            var expectedValue = info.GetValue(this.Value);
                            var extended = BuildFromField(this.MemberLongName, info.Name, info.FieldType, expectedValue,
                                this.Criteria);
                            result.Add(extended);
                        }
                    }
                    if (this.Criteria.WithProperties)
                    {
                        var fieldsInfo = currentType.GetProperties(this.Criteria.BindingFlags);
                        foreach (var info in fieldsInfo)
                        {
                            var expectedValue = info.GetValue(this.Value, null);
                            var extended = BuildFromProperty(this.MemberLongName,
                                info.Name, info.PropertyType, expectedValue, this.Criteria);
                            result.Add(extended);
                        }
                    }
                    currentType = currentType.GetBaseType();
                }
            }
            return result;
        }

        private ReflectionWrapper FindMember(ReflectionWrapper other)
        {
            var fields = this.GetSubExtendedMemberInfosFields();
            foreach (var info in fields)
            {
                if (other.nameInSource == info.nameInSource)
                {
                    return info;
                }
            }

            return null;
        }

        /// <summary>
        ///     The anonymous type field mask.
        /// </summary>
        private static readonly Regex AnonymousTypeFieldMask;

        /// <summary>
        ///     The auto property mask.
        /// </summary>
        private static readonly Regex AutoPropertyMask;

        /// <summary>
        ///     Initializes static members of the <see cref="ObjectFieldsCheckExtensions" /> class.
        /// </summary>
        static ReflectionWrapper()
        {
            AutoPropertyMask = new Regex("^<(.*)>k_");
            AnonymousTypeFieldMask = new Regex("^<(.*)>(i_|\\z)");
        }

        private static bool EvaluateCriteria(Regex expression, string name, out string actualFieldName)
        {
            var regTest = expression.Match(name);
            if (regTest.Groups.Count >= 2)
            {
                actualFieldName = name.Substring(regTest.Groups[1].Index, regTest.Groups[1].Length);
                return true;
            }

            actualFieldName = Empty;
            return false;
        }
    }
}