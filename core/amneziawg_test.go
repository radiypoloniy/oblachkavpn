package main

import (
	"context"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	box "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/option"
	"github.com/sagernet/sing/common/json"
	"github.com/sagernet/sing/service"
	"golang.org/x/crypto/curve25519"
)

func keyPair(t *testing.T) (private, public []byte) {
	private = make([]byte, 32)
	if _, err := rand.Read(private); err != nil {
		t.Fatal(err)
	}
	private[0] &= 248
	private[31] = (private[31] & 127) | 64
	public, err := curve25519.X25519(private, curve25519.Basepoint)
	if err != nil {
		t.Fatal(err)
	}
	return private, public
}

// Сервер AmneziaWG в памяти: устройство на loopback и HTTP-сервер внутри туннеля на 10.9.0.1:80.
func amneziaServer(t *testing.T, serverPrivate, clientPublic []byte, obfuscation string) int {
	tunDevice, stack, err := netstack.CreateNetTUN([]netip.Addr{netip.MustParseAddr("10.9.0.1")}, nil, 1280)
	if err != nil {
		t.Fatal(err)
	}
	listener, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := listener.LocalAddr().(*net.UDPAddr).Port
	listener.Close()
	wg := device.NewDevice(tunDevice, conn.NewDefaultBind(), device.NewLogger(device.LogLevelError, "server: "))
	t.Cleanup(wg.Close)
	err = wg.IpcSet(fmt.Sprintf("private_key=%s\nlisten_port=%d\n%spublic_key=%s\nallowed_ip=10.9.0.2/32\n",
		hex.EncodeToString(serverPrivate), port, obfuscation, hex.EncodeToString(clientPublic)))
	if err != nil {
		t.Fatal(err)
	}
	if err := wg.Up(); err != nil {
		t.Fatal(err)
	}
	web, err := stack.ListenTCPAddrPort(netip.MustParseAddrPort("10.9.0.1:80"))
	if err != nil {
		t.Fatal(err)
	}
	go http.Serve(web, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { io.WriteString(w, "amnezia ok") }))
	return port
}

// Ядро с выходом amneziawg и обычным прокси-входом; запрос через прокси к HTTP внутри туннеля.
func amneziaClient(t *testing.T, port int, clientPrivate, serverPublic []byte, obfuscation map[string]string) (string, error) {
	outboundOptions, _ := json.Marshal(map[string]any{
		"type": TypeAmneziaWG, "tag": "server", "server": "127.0.0.1", "server_port": port,
		"private_key": base64.StdEncoding.EncodeToString(clientPrivate), "peer_public_key": base64.StdEncoding.EncodeToString(serverPublic),
		"local_address": []string{"10.9.0.2/32"}, "obfuscation": obfuscation, "persistent_keepalive_interval": 25,
	})
	config := `{"log":{"level":"error"},"inbounds":[{"type":"mixed","listen":"127.0.0.1","listen_port":0,"tag":"in"}],"outbounds":[` + string(outboundOptions) + `]}`
	ctx, cancel := context.WithCancel(service.ExtendContext(globalContext()))
	t.Cleanup(cancel)
	options, err := json.UnmarshalExtendedContext[option.Options](ctx, []byte(config))
	if err != nil {
		t.Fatal(err)
	}
	listener, _ := net.Listen("tcp4", "127.0.0.1:0")
	proxyPort := listener.Addr().(*net.TCPAddr).Port
	listener.Close()
	options.Inbounds[0].Options.(*option.HTTPMixedInboundOptions).ListenPort = uint16(proxyPort)
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		t.Fatal(err)
	}
	if err := instance.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { instance.Close() })
	proxy, _ := url.Parse(fmt.Sprintf("http://127.0.0.1:%d", proxyPort))
	client := &http.Client{Transport: &http.Transport{Proxy: http.ProxyURL(proxy)}, Timeout: 8 * time.Second}
	response, err := client.Get("http://10.9.0.1/")
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	return string(body), err
}

var testObfuscation = map[string]string{"jc": "4", "jmin": "40", "jmax": "70", "s1": "15", "s2": "18", "h1": "1020325451", "h2": "3288052141", "h3": "1766607858", "h4": "2528465083"}

func uapiLines(m map[string]string) string {
	var b strings.Builder
	for k, v := range m {
		b.WriteString(k + "=" + v + "\n")
	}
	return b.String()
}

func TestAmneziaWGCarriesTraffic(t *testing.T) {
	serverPrivate, serverPublic := keyPair(t)
	clientPrivate, clientPublic := keyPair(t)
	port := amneziaServer(t, serverPrivate, clientPublic, uapiLines(testObfuscation))
	body, err := amneziaClient(t, port, clientPrivate, serverPublic, testObfuscation)
	if err != nil || body != "amnezia ok" {
		t.Fatalf("request through amneziawg: %q, %v", body, err)
	}
}

// Несовпадение заголовков H1–H4 — сервер не узнаёт рукопожатие: значит, обфускация и правда применяется.
func TestAmneziaWGObfuscationMustMatch(t *testing.T) {
	serverPrivate, serverPublic := keyPair(t)
	clientPrivate, clientPublic := keyPair(t)
	port := amneziaServer(t, serverPrivate, clientPublic, uapiLines(testObfuscation))
	wrong := map[string]string{}
	for k, v := range testObfuscation {
		wrong[k] = v
	}
	wrong["h1"] = "1111111111"
	if body, err := amneziaClient(t, port, clientPrivate, serverPublic, wrong); err == nil {
		t.Fatalf("handshake with wrong H1 succeeded: %q", body)
	}
}

func TestAmneziaWGRefusesInjectedLines(t *testing.T) {
	_, public := keyPair(t)
	private, _ := keyPair(t)
	options := AmneziaWGOptions{Server: "127.0.0.1", ServerPort: 1, PrivateKey: base64.StdEncoding.EncodeToString(private), PeerPublicKey: base64.StdEncoding.EncodeToString(public)}
	for _, bad := range []map[string]string{{"jc": "4\nallowed_ip=1.2.3.4/32"}, {"listen_port": "53"}, {"endpoint": "1.2.3.4:5"}, {"jc": "a=b"}} {
		options.Obfuscation = bad
		if _, err := amneziaUAPI(options); err == nil {
			t.Fatalf("accepted %v", bad)
		}
	}
}
