#!/usr/bin/env bash
# Compiles and RUNS the portable subset of Core against the modern .NET runtime.
#
# Why this exists: the product targets .NET Framework 4.0 and only runs on Windows, so on a
# Linux/macOS build machine the net40 output can be compiled but never executed. The files
# below touch no Win32 API at runtime, so they can be built for net10 and actually run -
# which is the only way to get real test results for the path-traversal rejection, the
# manifest escaping and the glob matcher without a Windows box.
#
# It is a verification aid, not part of the shipped build. build.sh is the real build.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/mtnpc-verify-pure"
rm -rf "$WORK"
mkdir -p "$WORK"

cat > "$WORK/VerifyPure.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AssemblyName>VerifyPure</AssemblyName>
    <RootNamespace>MoveToNewPC.Tests</RootNamespace>
    <StartupObject>MoveToNewPC.Tests.PureProgram</StartupObject>
    <NoWarn>$(NoWarn);CS0618;CS0649;SYSLIB0003;CA1416;CS8981</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="SRC/src/MoveToNewPC.Core/Native/NativeMethods.cs" />
    <Compile Include="SRC/src/MoveToNewPC.Core/IO/LongPath.cs" />
    <Compile Include="SRC/src/MoveToNewPC.Core/IO/PathValidation.cs" />
    <Compile Include="SRC/src/MoveToNewPC.Core/Util/Glob.cs" />
    <Compile Include="SRC/src/MoveToNewPC.Core/Util/Format.cs" />
    <Compile Include="SRC/src/MoveToNewPC.Core/Manifests/ManifestText.cs" />
    <Compile Include="SRC/tests/MoveToNewPC.Tests/TestHarness.cs" />
    <Compile Include="SRC/tests/MoveToNewPC.Tests/PureTests.cs" />
    <Compile Include="SRC/tests/MoveToNewPC.Tests/PureMain.cs" />
  </ItemGroup>
</Project>
EOF

sed -i "s|SRC|$ROOT|g" "$WORK/VerifyPure.csproj"

echo "==> building the portable subset for net10"
dotnet build "$WORK/VerifyPure.csproj" -c Release -v quiet --nologo -o "$WORK/out" > "$WORK/build.log" 2>&1 || {
  echo "BUILD FAILED"; cat "$WORK/build.log"; exit 1;
}

echo "==> running"
dotnet "$WORK/out/VerifyPure.dll" "$@"
