#!/usr/bin/env python

import platform
import os
import os.path
import unittest
import subprocess
import glob
import sys
import shutil

def compile_cs(fileName):
  if platform.system() == "Windows":
    csc = "csc"
  else:
    csc = "mcs"

  testName = os.path.basename(fileName)
  compiledPath = os.path.join("output", testName.replace(".cs", ".exe"))

  inTime = os.path.getmtime(fileName)
  try:
    outTime = os.path.getmtime(compiledPath)
    if (outTime > inTime):
      return (0, compiledPath)
  except OSError:
    pass

  commandStr = ("%s /nologo /langversion:6 /debug+ /debug:full %s /reference:%s /out:%s") % (
    csc, fileName, 
    os.path.join("WasmMeta", "bin", "WasmMeta.dll"),
    compiledPath
  )
  exitCode = subprocess.call(commandStr, shell=True)

  if exitCode != 0:
    print("failed while running '%s'" % commandStr)
    print("")

  return (exitCode, compiledPath)

def translate(compiledPath):
  wasmPath = compiledPath.replace(".exe", ".wasm")

  inTime = os.path.getmtime(compiledPath)
  try:
    outTime = os.path.getmtime(wasmPath)
    if (outTime > inTime):
      return (0, wasmPath)
  except OSError:
    pass

  commandStr = ("%s ilwasm.jsilconfig --quiet --nodefaults --e=WasmSExpr --outputFile=%s %s") % (
    os.path.join("third_party", "JSIL", "bin", "JSILc.exe"),
    wasmPath, compiledPath
  )
  exitCode = subprocess.call(commandStr, shell=True)

  if exitCode != 0:
    print("failed while running '%s'" % commandStr)
    print("")

  return (exitCode, wasmPath)

def run_csharp(compiledPath):
  commandStr = ("%s --quiet") % (compiledPath)
  exitCode = subprocess.call(commandStr, shell=True)

  if exitCode != 0:
    print("failed while running '%s'" % commandStr)
    print("")

  return exitCode

def run_wasm(wasmPath):
  interpreterPath = os.path.realpath(os.path.join("..", "wasm-spec", "ml-proto", "src", "_build", "main.native"))
  if not os.path.exists(interpreterPath):
    interpreterPath = os.path.realpath(os.path.join("..", "wasm-spec", "ml-proto", "src", "_build", "host", "main.native"))

  commandStr = ("%s %s") % (interpreterPath, wasmPath)
  exitCode = subprocess.call(commandStr, shell=True)

  if exitCode != 0:
    print("failed while running '%s'" % commandStr)
    print("")

  return exitCode


class RunTests(unittest.TestCase):
  def _runTestFile(self, fileName):
    (exitCode, compiledPath) = compile_cs(fileName)
    self.assertEqual(0, exitCode, "C# compiler failed with exit code %i" % exitCode)
    (exitCode, wasmPath) = translate(compiledPath)
    self.assertEqual(0, exitCode, "JSILc failed with exit code %i" % exitCode)
    exitCode = run_csharp(compiledPath)
    self.assertEqual(0, exitCode, "C# test case failed with exit code %i" % exitCode)
    exitCode = run_wasm(wasmPath)
    self.assertEqual(0, exitCode, "wasm interpreter failed with exit code %i" % exitCode)

def generate_test_case(fileName):
  return lambda self : self._runTestFile(fileName)


def generate_test_cases(cls, files):
  for fileName in files:
    testCase = generate_test_case(fileName)
    setattr(cls, fileName, testCase)

if __name__ == "__main__":
  try:
    os.makedirs("output/")
  except OSError:
    pass

  testFiles = glob.glob(os.path.join("third_party", "tests", "*.cs"))
  unittest.TestLoader.testMethodPrefix = os.path.join("third_party", "tests")
  generate_test_cases(RunTests, testFiles)
  shutil.copy2(os.path.join("WasmMeta", "bin", "WasmMeta.dll"), "output")
  unittest.main()
