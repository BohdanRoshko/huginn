// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Huginn",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        // Pure Foundation logic, no AppKit/AVFoundation. Split out so it can be
        // exercised by `swift test` in CI — with no Mac on the authoring side,
        // this is the only part of the app that can be verified automatically.
        .target(
            name: "HuginnCore",
            path: "Sources/HuginnCore"
        ),
        .executableTarget(
            name: "Huginn",
            dependencies: ["HuginnCore"],
            path: "Sources/Huginn"
        ),
        .testTarget(
            name: "HuginnCoreTests",
            dependencies: ["HuginnCore"],
            path: "Tests/HuginnCoreTests"
        )
    ]
)
