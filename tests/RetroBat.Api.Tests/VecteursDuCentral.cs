namespace RetroBat.Api.Tests;

/// <summary>
/// Vecteurs produits par le central (app/Reseau du site, PHP, cles d'essai) : une requete scellee comme la
/// borne la scelle, sa reponse scellee, un verdict signe et une carte signee. La borne doit ouvrir ce que le
/// central ecrit, a l'octet pres.
/// </summary>
internal static class VecteursDuCentral
{
    public const string Json = """
        {
            "seal_private": "-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgCn9hcwINbKZ0aYHd\nz9sKz79MxHOFSXJBIzThUeCk3qihRANCAATU3Vo5HiC/LLJLfnprw3NBhACcewX+\nr2UWKaOs1ral4uEXupRgH/ISaK/V83+lP+JVrgWEywqiCgQIMMUPsshh\n-----END PRIVATE KEY-----\n",
            "seal_public": "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1N1aOR4gvyyyS356a8NzQYQAnHsF\n/q9lFimjrNa2peLhF7qUYB/yEmiv1fN/pT/iVa4FhMsKogoECDDFD7LIYQ==\n-----END PUBLIC KEY-----\n",
            "sealed_request": {
                "format": 1,
                "kind": "nelfeplay-sealed",
                "alg": "ECDH_P256_HKDF_SHA256_A256GCM",
                "to": "8bd42766c9b20292e32202ccbaf3d96f62a119d9782fbc861451b4e9a4efb8ce",
                "id": "c876b04f1de0cb0c8d9890ca9bc292fc",
                "epk": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAECERIiuJgoKFmTLTFM_OetDMG0x9udAjauihVsDL1Le_VSG0jZHXrD4pzKVBET_RAbeLhvJhaG_zDSSNsnMZ1FA",
                "iv": "k9dorBpCenO-QTji",
                "ct": "T5ls1yXiJuw6GnA7HnWVJbCUMG76c46MdLNRx-Aa5juOl8MNW5zkhy8eQHTr7QejYKnSJFB-RiRo09QPaqAJg2EhTqP-Qqqxplujr-IhlfqtKeJizJoebp0SNkzi554BQf-051h7vvgzUqycYlZV2hhFridJy_fvKqDmvXn_It8CCut-sAUeWz79PLHAZ0joz8yC8t_tRgES_oTJzQN9QeVb6BPZHu53cxmWBJS2qZBk6k5vNWfDFXqoUx4MsVsUFlZl6rZBNoM0uAgA",
                "keep": true,
                "pub": "eyJzZXNzaW9uX2lkIjoicy0xIiwic2NvcmUiOiIxMjAwIn0"
            },
            "request": {
                "method": "POST",
                "path": "scores/submissions",
                "query": "",
                "device": "cred-essai",
                "label": "",
                "body": "{\"session_id\":\"s-1\",\"metric\":{\"value\":\"1200\"}}",
                "sent_at": "2026-10-08T21:00:00Z"
            },
            "reply_key_hex": "59f60a2fc4cb3ec651e2a25e211d0b5d301ff1edc2c2b2bf9c87a6b82296d68f",
            "sealed_reply": {
                "format": 1,
                "kind": "nelfeplay-sealed-reply",
                "id": "c876b04f1de0cb0c8d9890ca9bc292fc",
                "iv": "szVgMal_0LUwSidQ",
                "ct": "A4KSrec0aykcJVpoQfVQZAyrzeEfVLHaBlhYbcPhcQi38fkT-kYGDdp1zRhYObgEu7A4am1HN7XiPgJDUixbdA-0GwXKuma8Yj8D2m-1e9C1t_cBe55xqWNAJvgidW13MxyYww9084UOzTh6"
            },
            "reply": {
                "status": 200,
                "retry_after": null,
                "body": "{\"ok\":true,\"status\":\"published\",\"rank\":3}"
            },
            "issuer_public": "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYwnbfuYtL0rSbQdnTG72a6dtN0di\noMUJK4dXBflfLEI3tl/ncC5LkqB7qSuwdsd/sxi50jHtkdQ5oQGLkz0xPg==\n-----END PUBLIC KEY-----\n",
            "passport": "{\"protocol\":1,\"session_id\":\"s-1\",\"device\":{\"device_id\":\"d_9\"},\"game\":{\"system_id\":\"arcade\",\"rom_group\":\"dino\",\"ruleset\":\"1cc\"},\"metric\":{\"value\":\"1200\"}}",
            "passport_response": "{\"ok\":true,\"status\":\"published\",\"reason\":\"\",\"rank\":3,\"claim_code\":null,\"verdict\":{\"format\":1,\"alg\":\"ECDSA_P256_SHA256_DER\",\"key_id\":\"8bcbc1dc91426b1e9dfdcaec5e810749e787fa95876cf08188642afba65efe95\",\"payload\":\"eyJmb3JtYXQiOjEsImtpbmQiOiJuZWxmZXBsYXktdmVyZGljdCIsInNlc3Npb25faWQiOiJzLTEiLCJkZXZpY2VfaWQiOiJkXzkiLCJib2R5X3NoYTI1NiI6IjE5YWYwOTgzOTI4YzI4NjU2YTllOGVlY2NjNDRkYjA0MmEwODM2NGM0NmZkMDIyYjY0NGY0NjI2N2EyZjEwNTUiLCJzdGF0dXMiOiJwdWJsaXNoZWQiLCJyZWFzb24iOiIiLCJvcmlnaW5hbF9zdGF0dXMiOm51bGwsIm9yaWdpbmFsX3JlYXNvbiI6bnVsbCwicmFuayI6MywiZmxhZ3MiOltdLCJnYW1lIjp7InN5c3RlbV9pZCI6ImFyY2FkZSIsInJvbV9ncm91cCI6ImRpbm8iLCJydWxlc2V0IjoiMWNjIn0sInNjb3JlIjoiMTIwMCIsImlzc3VlZF9hdCI6IjIwMjYtMTAtMDdUMDA6MDA6MDBaIn0\",\"signature\":\"MEUCIAFyn30Dtmd3tIZ_LgtUfrOx_GklgRN1VJgJc-C8br3kAiEA6iXXF1qm-CmqNIVP3oz2jVXV86mrwbgAksX_zMKmNx0\"}}",
            "site_public": "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEX4P2rO5LsPUrl5mRHuXm9VoUgnWV\neNwUkFi+l0L/WenyYVYWqbRG5fyEzIvH8E5GrSb3Vxo60LLGNYWFvAloug==\n-----END PUBLIC KEY-----\n",
            "map": "{\"format\":1,\"alg\":\"ECDSA_P256_SHA256_DER\",\"key_id\":\"5cc5c16dd2437e0399b55fde950cf613e05ca977ae9894dd02448fbad3d83aba\",\"payload\":\"eyJmb3JtYXQiOjEsImtpbmQiOiJuZWxmZXBsYXktbWFwIiwidmVyc2lvbiI6MTc5MTMzMTIwMCwiaXNzdWVkX2F0IjoiMjAyNi0xMC0wN1QwMDowMDowMFoiLCJjZW50cmFsIjp7InVybCI6Imh0dHBzOi8vbmVsZmVwbGF5LmNvbSIsInNlYWxfa2V5IjoiLS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS1cbk1Ga3dFd1lIS29aSXpqMENBUVlJS29aSXpqMERBUWNEUWdBRTFOMWFPUjRndnl5eVMzNTZhOE56UVlRQW5Ic0Zcbi9xOWxGaW1qck5hMnBlTGhGN3FVWUIveUVtaXYxZk4vcFQvaVZhNEZoTXNLb2dvRUNEREZEN0xJWVE9PVxuLS0tLS1FTkQgUFVCTElDIEtFWS0tLS0tXG4iLCJzZWFsX2tleV9pZCI6IjhiZDQyNzY2YzliMjAyOTJlMzIyMDJjY2JhZjNkOTZmNjJhMTE5ZDk3ODJmYmM4NjE0NTFiNGU5YTRlZmI4Y2UiLCJ2ZXJkaWN0X2tleXMiOlsiLS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS1cbk1Ga3dFd1lIS29aSXpqMENBUVlJS29aSXpqMERBUWNEUWdBRVl3bmJmdVl0TDByU2JRZG5URzcyYTZkdE4wZGlcbm9NVUpLNGRYQmZsZkxFSTN0bC9uY0M1TGtxQjdxU3V3ZHNkL3N4aTUwakh0a2RRNW9RR0xrejB4UGc9PVxuLS0tLS1FTkQgUFVCTElDIEtFWS0tLS0tXG4iXX0sInJlcGxheSI6eyJjb3BpZXMiOjJ9LCJub2RlcyI6W3siaWQiOjEsIm5hbWUiOiJtaXJvaXIiLCJraW5kIjoic3RhdGljIiwidXJsIjoiaHR0cHM6Ly9taXJvaXIubmVsZmVwbGF5LmNvbSIsImtleV9pZCI6bnVsbCwicHVibGljX2tleSI6bnVsbCwicm9sZXMiOlsiZnJvbnQiXSwicmVnaW9uIjoiRXVyb3BlIiwiY291bnRyeSI6IkZSIiwiaG9zdCI6ImdpdGh1YiIsIndlaWdodCI6MX0seyJpZCI6NywibmFtZSI6ImV1Iiwia2luZCI6Im5vZGUiLCJ1cmwiOiJodHRwczovL25lbGZlcGxheS5ldSIsImtleV9pZCI6IjQwMGU2ODFjMWFkNzJhNTZjMmI3NDkxMTllNmRhZTFjMTE2MjcwNmM2ZTdlZjU0OWQ1OTVmMzk4NWMzOTRkYzgiLCJwdWJsaWNfa2V5IjoiLS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS1cbk1Ga3dFd1lIS29aSXpqMENBUVlJS29aSXpqMERBUWNEUWdBRSsrZ1k1eThqSHNTZHU1aU9MWDJRR0dkUnFSVFJcbldtZFJpUFdaVG5yaUlYQ1dGS0RMVHdibnZZVjdDL0RqZTFMaC9qRWV6bzhyT1JtK2lhTm03UVpuK1E9PVxuLS0tLS1FTkQgUFVCTElDIEtFWS0tLS0tXG4iLCJyb2xlcyI6WyJmcm9udCIsInJlbGF5IiwicmVwbGF5Il0sInJlZ2lvbiI6IkV1cm9wZSIsImNvdW50cnkiOiJGUiIsImhvc3QiOiJuZWxmZXBsYXkiLCJ3ZWlnaHQiOjN9XX0\",\"signature\":\"MEQCIGHo-igGmgQLnp8ETvjy37j_Ov9zlLXae0jAQXYI3rVhAiA2PNy9t7ifg5FRsKg5bgDKhgaSg-PDmyIwHd-rb8Iw0w\"}"
        }
        """;
}
