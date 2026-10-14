#!/bin/sh
# Seeds the local k3s cluster (profile: kubernetes) with the demo states the Kubernetes plugin's
# mockup shows. Idempotent: everything is `kubectl apply`, so re-running it is harmless.
# The tokens are local-only demo values (docker/kubernetes/tokens.csv) -- never reuse them.
set -eu

K="kubectl --server=$KUBE_SERVER --token=$KUBE_TOKEN --insecure-skip-tls-verify"

# k3s applies 01-rbac.yaml itself from its auto-deploy manifests directory (see docker-compose.yml);
# until it has, the admin token can do nothing. Wait for it (up to 3 minutes).
i=0
until [ "$($K auth can-i '*' '*' --all-namespaces 2>/dev/null)" = "yes" ]; do
  i=$((i + 1))
  [ "$i" -le 90 ] || { echo "sbconsole-admin never got cluster-admin" >&2; exit 1; }
  sleep 2
done

$K apply -f /work/seed/00-namespaces.yaml
# 01-rbac.yaml is not applied here: k3s owns it (auto-deploy manifest).
$K apply -f /work/seed/10-checkout.yaml -f /work/seed/11-payments.yaml \
  -f /work/seed/12-platform.yaml -f /work/seed/13-monitoring.yaml -f /work/seed/20-config.yaml

# alpine/k8s ships kubectl but not openssl; install it on first use (needs network once per run).
command -v openssl >/dev/null 2>&1 || apk add --no-cache -q openssl

# A fresh self-signed cert on every run, so checkout-tls always expires ~9 days out
# (inside the plugin's 30-day warning window, outside the 7-day error one).
openssl req -x509 -newkey rsa:2048 -nodes -days 9 -subj /CN=checkout.local \
  -keyout /tmp/tls.key -out /tmp/tls.crt 2>/dev/null
$K -n checkout create secret tls checkout-tls --cert /tmp/tls.crt --key /tmp/tls.key \
  --dry-run=client -o yaml | $K apply -f -
rm -f /tmp/tls.key /tmp/tls.crt

# v1 must be fully rolled out before v2 lands, so the bad rollout leaves the old ReplicaSet
# serving (maxUnavailable: 0) and the Deployment reports ProgressDeadlineExceeded.
# rollout status gives up as soon as the 60 s progress deadline passes, which a slow first image
# pull can trip even for v1, so retry it for up to 5 minutes.
i=0
until $K -n checkout rollout status deploy/payment-gateway --timeout=300s; do
  i=$((i + 1))
  [ "$i" -le 30 ] || { echo "payment-gateway v1 never became ready" >&2; exit 1; }
  sleep 10
done
$K apply -f /work/seed/30-payment-gateway-v2.yaml

echo done
