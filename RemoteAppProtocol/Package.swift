// swift-tools-version:5.9
// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import PackageDescription

let package = Package(
    name: "RemoteAppProtocol",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .library(name: "RemoteAppProtocol", targets: ["RemoteAppProtocol"]),
    ],
    targets: [
        .target(name: "RemoteAppProtocol"),
        .testTarget(name: "RemoteAppProtocolTests", dependencies: ["RemoteAppProtocol"]),
    ],
    swiftLanguageVersions: [.v5]
)
