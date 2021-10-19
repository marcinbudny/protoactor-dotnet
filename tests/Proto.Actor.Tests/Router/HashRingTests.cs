﻿// -----------------------------------------------------------------------
// <copyright file="HashRingTests.cs" company="Asynkron AB">
//      Copyright (C) 2015-2021 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace Proto.Router.Tests
{
    public class HashRingTests
    {
        [Fact]
        public void Can_provide_consistent_results()
        {
            var values = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
            var hashRing = new HashRing<string>(values, value => value, MurmurHash2.Hash, 20);

            var val = Guid.NewGuid().ToString("N");

            var node = hashRing.GetNode(val);
            var node2 = hashRing.GetNode(val);
            node.Should().Be(node2);
        }
    }
}