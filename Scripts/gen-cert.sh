#!/bin/sh
# Generates a self-signed TLS identity for the prototype server and writes it
# as Server/Resources/server.p12 (passphrase "remoteapp"). Dev only: the client
# does not verify the certificate chain.
set -eu
cd "$(dirname "$0")/.."
TMP=$(mktemp -d)
/usr/bin/openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
  -keyout "$TMP/key.pem" -out "$TMP/cert.pem" \
  -subj "/CN=RemoteApp Server/O=RemoteApp Prototype" \
  -addext "subjectAltName=DNS:remoteapp.local" 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$TMP/key.pem" -in "$TMP/cert.pem" \
  -name "RemoteApp Server" -passout pass:remoteapp \
  -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 \
  -out Server/Resources/server.p12
rm -rf "$TMP"
echo "wrote Server/Resources/server.p12"
