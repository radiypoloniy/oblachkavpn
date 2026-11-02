package main

import (
	"context"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"net"
	"net/http/httptest"
	"testing"
	"time"

	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/adapter"
	M "github.com/sagernet/sing/common/metadata"
)

type hysteriaFixture struct {
	port        int
	password    string
	certificate string
	obfs        map[string]any
}

func hysteriaBox(t *testing.T, config map[string]any) *box.Box {
	t.Helper()
	config["log"] = map[string]any{"disabled": true}
	content, err := json.Marshal(config)
	if err != nil {
		t.Fatal(err)
	}
	ctx := globalContext()
	options, err := decodeOptions(ctx, content)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { instance.Close() })
	if err := instance.Start(); err != nil {
		t.Fatal(err)
	}
	return instance
}

func hysteriaServer(t *testing.T, obfuscate bool) hysteriaFixture {
	t.Helper()
	certSource := httptest.NewTLSServer(nil)
	defer certSource.Close()
	certificate := string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: certSource.Certificate().Raw}))
	key, err := x509.MarshalPKCS8PrivateKey(certSource.TLS.Certificates[0].PrivateKey)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := listener.LocalAddr().(*net.UDPAddr).Port
	listener.Close()
	f := hysteriaFixture{port: port, password: fixtureSecret(t), certificate: certificate}
	inbound := map[string]any{
		"type": "hysteria2", "listen": "127.0.0.1", "listen_port": port,
		"users": []any{map[string]any{"password": f.password}},
		"tls": map[string]any{"enabled": true, "certificate": []string{certificate},
			"key": []string{string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: key}))}},
	}
	if obfuscate {
		f.obfs = map[string]any{"type": "salamander", "password": fixtureSecret(t)}
		inbound["obfs"] = f.obfs
	}
	hysteriaBox(t, map[string]any{"inbounds": []any{inbound}, "outbounds": []any{map[string]any{"type": "direct", "tag": "exit"}}})
	return f
}

func hysteriaClient(t *testing.T, f hysteriaFixture, serverName, password string) adapter.Outbound {
	t.Helper()
	outbound := map[string]any{"type": "hysteria2", "tag": "direct-0", "server": "127.0.0.1", "server_port": f.port,
		"password": password, "tls": map[string]any{"enabled": true, "server_name": serverName, "alpn": []string{"h3"}}}
	if f.obfs != nil {
		outbound["obfs"] = f.obfs
	}
	instance := hysteriaBox(t, map[string]any{
		// Only this test's instance trusts the fixture certificate. TLS verification stays enabled.
		"certificate": map[string]any{"store": "none", "certificate": []string{f.certificate}},
		"outbounds": []any{outbound,
			map[string]any{"type": "socks", "tag": "local-core", "server": "127.0.0.1", "server_port": 1},
			map[string]any{"type": "selector", "tag": "server-selector", "outbounds": []string{"direct-0", "local-core"}, "default": "direct-0"}},
		"route": map[string]any{"final": "server-selector"},
	})
	selected, found := instance.Outbound().Outbound("server-selector")
	if !found {
		t.Fatal("missing VPN selector")
	}
	return selected
}

func hysteriaEcho(t *testing.T) (string, string) {
	t.Helper()
	tcp, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	udp, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { tcp.Close(); udp.Close() })
	go func() {
		for {
			c, err := tcp.Accept()
			if err != nil {
				return
			}
			go func() { defer c.Close(); c.SetDeadline(time.Now().Add(8 * time.Second)); io.Copy(c, c) }()
		}
	}()
	go func() {
		buffer := make([]byte, 4096)
		for {
			n, address, err := udp.ReadFrom(buffer)
			if err != nil {
				return
			}
			udp.WriteTo(buffer[:n], address)
		}
	}()
	return tcp.Addr().String(), udp.LocalAddr().String()
}

func hysteriaRoundTrip(outbound adapter.Outbound, network, address string) error {
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	connection, err := outbound.DialContext(ctx, network, M.ParseSocksaddr(address))
	if err != nil {
		return err
	}
	defer connection.Close()
	connection.SetDeadline(time.Now().Add(4 * time.Second))
	payload := []byte("native hysteria loopback echo")
	if _, err = connection.Write(payload); err != nil {
		return err
	}
	response := make([]byte, len(payload))
	if _, err = io.ReadFull(connection, response); err != nil {
		return err
	}
	if string(response) != string(payload) {
		return fmt.Errorf("echo data mismatch")
	}
	return nil
}

func TestNativeHysteriaCarriesTCPAndUDP(t *testing.T) {
	for _, obfuscate := range []bool{false, true} {
		t.Run(fmt.Sprintf("salamander=%v", obfuscate), func(t *testing.T) {
			f := hysteriaServer(t, obfuscate)
			outbound := hysteriaClient(t, f, "127.0.0.1", f.password)
			tcp, udp := hysteriaEcho(t)
			if err := hysteriaRoundTrip(outbound, "tcp", tcp); err != nil {
				t.Fatal(err)
			}
			if err := hysteriaRoundTrip(outbound, "udp", udp); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestNativeHysteriaRejectsWrongIdentityAndAuthentication(t *testing.T) {
	for _, wrongName := range []bool{true, false} {
		t.Run(fmt.Sprintf("wrong-name=%v", wrongName), func(t *testing.T) {
			f := hysteriaServer(t, false)
			name, password := "127.0.0.1", f.password
			if wrongName {
				name = "wrong.fixture.invalid"
			} else {
				password = fixtureSecret(t)
			}
			tcp, _ := hysteriaEcho(t)
			if err := hysteriaRoundTrip(hysteriaClient(t, f, name, password), "tcp", tcp); err == nil {
				t.Fatal("invalid identity passed via VPN or fallback")
			}
		})
	}
}
