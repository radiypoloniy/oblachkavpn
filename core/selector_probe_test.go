package main

import (
	"context"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	box "github.com/sagernet/sing-box"
)

var fixtureRoots sync.Once

// With a direct final route, a broken VPN selector must still fail its health
// check. Changing the selector to a working outbound must then succeed.
func TestSelectorProbeIgnoresDirectFinalRoute(t *testing.T) {
	target := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { time.Sleep(5 * time.Millisecond); w.WriteHeader(204) }))
	defer target.Close()
	// Trust the fixture CA only inside this test process; keep certificate
	// verification enabled. The upstream HTTP /delay uses a background context.
	t.Setenv("GODEBUG", os.Getenv("GODEBUG")+",x509usefallbackroots=1")
	fixtureRoots.Do(func() { roots := x509.NewCertPool(); roots.AddCert(target.Certificate()); x509.SetFallbackRoots(roots) })
	port := func() int {
		l, err := net.Listen("tcp4", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		p := l.Addr().(*net.TCPAddr).Port
		l.Close()
		return p
	}
	apiPort, deadPort := port(), port()
	secret := fixtureSecret(t)
	certificate := string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: target.Certificate().Raw}))
	config, _ := json.Marshal(map[string]any{
		"log":         map[string]any{"disabled": true},
		"certificate": map[string]any{"store": "none", "certificate": []string{certificate}},
		"outbounds": []any{
			map[string]any{"type": "direct", "tag": "user-direct"},
			map[string]any{"type": "socks", "tag": "broken-vpn", "server": "127.0.0.1", "server_port": deadPort},
			map[string]any{"type": "selector", "tag": "server-selector", "outbounds": []string{"broken-vpn", "user-direct"}, "default": "broken-vpn"},
		},
		"route":        map[string]any{"final": "user-direct"},
		"experimental": map[string]any{"clash_api": map[string]any{"external_controller": fmt.Sprintf("127.0.0.1:%d", apiPort), "secret": secret}},
	})
	ctx := globalContext()
	options, err := decodeOptions(ctx, config)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		t.Fatal(err)
	}
	defer instance.Close()
	if err := instance.Start(); err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Transport: &http.Transport{Proxy: nil}}
	defer client.CloseIdleConnections()
	request := func(method, path, body string) int {
		r, _ := http.NewRequestWithContext(context.Background(), method, fmt.Sprintf("http://127.0.0.1:%d%s", apiPort, path), strings.NewReader(body))
		r.Header.Set("Authorization", "Bearer "+secret)
		r.Header.Set("Content-Type", "application/json")
		response, err := client.Do(r)
		if err != nil {
			t.Fatal(err)
		}
		defer response.Body.Close()
		return response.StatusCode
	}
	probe := "/proxies/server-selector/delay?timeout=2000&url=" + url.QueryEscape(target.URL)
	if got := request("GET", probe, ""); got != 503 {
		t.Fatalf("broken VPN passed via direct final route: HTTP %d", got)
	}
	if got := request("PUT", "/proxies/server-selector", `{"name":"user-direct"}`); got != 204 {
		t.Fatal(got)
	}
	if got := request("GET", probe, ""); got != 200 {
		t.Fatalf("working selected outbound failed: HTTP %d", got)
	}
}
