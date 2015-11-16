﻿using System.Linq;
using NUnit.Framework;

namespace NFig.Tests
{
    [TestFixture]
    public class CustomConverterTests
    {
        [Test]
        public void CustomConverterTest()
        {
            var factory = new SettingsFactory<CustomConverterSettings, Tier, DataCenter>();
            var s = factory.GetAppSettings(Tier.Local, DataCenter.Local);

            Assert.True(s.Ints != null, "Ints should not be null");
            Assert.True(s.Ints.Length == 3, "Ints should have length of 3, but is length " + s.Ints.Length);
            Assert.AreEqual(s.Ints[0], 2);
            Assert.AreEqual(s.Ints[1], 3);
            Assert.AreEqual(s.Ints[2], 4);
        }

        private class CustomConverterSettings : SettingsBase
        {
            [Setting("2,3,4")]
            [SettingConverter(typeof(IntArrayConverter))]
            public int[] Ints { get; private set; }
        }

        public class IntArrayConverter : ISettingConverter<int[]>
        {
            public string GetString(int[] value)
            {
                return string.Join(",", value);
            }

            public int[] GetValue(string str)
            {
                return str.Split(',').Select(s => int.Parse(s)).ToArray();
            }
        }
    }
}